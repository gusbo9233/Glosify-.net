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
        if (!Pkce.IsValidChallenge(codeChallenge))
        {
            throw new ArgumentException("A valid S256 PKCE code challenge is required.", nameof(codeChallenge));
        }

        var lifetime = TimeSpan.FromSeconds(Math.Clamp(
            _options.AuthorizationCodeLifetimeSeconds,
            30,
            300));
        var code = Pkce.CreateAuthorizationCode();
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
            || !Pkce.Matches(entry.CodeChallenge, codeVerifier))
        {
            return false;
        }

        userId = entry.UserId;
        return true;
    }

    internal static string CreateCodeChallenge(string codeVerifier) => Pkce.CreateChallenge(codeVerifier);

    private sealed record AuthorizationCodeEntry(
        string UserId,
        string RedirectUri,
        string CodeChallenge,
        DateTimeOffset ExpiresAt);
}
