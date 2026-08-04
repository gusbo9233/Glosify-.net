using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Auth;

public interface IExtensionAuthorizationCodeStore
{
    string Create(string userId, string redirectUri, string codeChallenge);
    bool TryRedeem(string code, string redirectUri, string codeVerifier, out string userId);
}

public sealed class ExtensionAuthorizationCodeStore : IExtensionAuthorizationCodeStore
{
    private const string CacheKeyPrefix = "extension-auth-code:";

    private readonly IMemoryCache _cache;
    private readonly ExtensionAuthOptions _options;
    private readonly TimeProvider _timeProvider;

    public ExtensionAuthorizationCodeStore(
        IMemoryCache cache,
        IOptions<ExtensionAuthOptions> options,
        TimeProvider timeProvider)
    {
        _cache = cache;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public string Create(string userId, string redirectUri, string codeChallenge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (!_options.IsAllowedRedirectUri(redirectUri))
        {
            throw new ArgumentException("The extension redirect URI is not allowed.", nameof(redirectUri));
        }
        if (!IsValidCodeChallenge(codeChallenge))
        {
            throw new ArgumentException("A valid S256 PKCE code challenge is required.", nameof(codeChallenge));
        }

        var lifetime = TimeSpan.FromSeconds(Math.Clamp(
            _options.AuthorizationCodeLifetimeSeconds,
            30,
            300));
        var code = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var entry = new AuthorizationCodeEntry(
            userId,
            redirectUri,
            codeChallenge,
            _timeProvider.GetUtcNow().Add(lifetime));
        _cache.Set(CacheKeyPrefix + code, entry, lifetime);
        return code;
    }

    public bool TryRedeem(string code, string redirectUri, string codeVerifier, out string userId)
    {
        userId = string.Empty;
        if (string.IsNullOrWhiteSpace(code)
            || !_cache.TryGetValue(CacheKeyPrefix + code, out AuthorizationCodeEntry? entry)
            || entry is null)
        {
            return false;
        }

        // Consume before validation so a guessed verifier cannot be retried against
        // the same authorization code.
        _cache.Remove(CacheKeyPrefix + code);
        if (_timeProvider.GetUtcNow() > entry.ExpiresAt
            || !string.Equals(entry.RedirectUri, redirectUri, StringComparison.Ordinal)
            || !IsValidCodeVerifier(codeVerifier))
        {
            return false;
        }

        var actualChallenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        var expectedBytes = Encoding.ASCII.GetBytes(entry.CodeChallenge);
        var actualBytes = Encoding.ASCII.GetBytes(actualChallenge);
        if (expectedBytes.Length != actualBytes.Length
            || !CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
        {
            return false;
        }

        userId = entry.UserId;
        return true;
    }

    internal static string CreateCodeChallenge(string codeVerifier) =>
        Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

    internal static bool IsValidCodeVerifier(string value) =>
        value is { Length: >= 43 and <= 128 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~');

    private static bool IsValidCodeChallenge(string? value) =>
        value is { Length: 43 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed record AuthorizationCodeEntry(
        string UserId,
        string RedirectUri,
        string CodeChallenge,
        DateTimeOffset ExpiresAt);
}
