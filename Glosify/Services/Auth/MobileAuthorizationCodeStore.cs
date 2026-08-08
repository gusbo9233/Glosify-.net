using Microsoft.Extensions.Caching.Memory;

namespace Glosify.Services.Auth;

public interface IMobileAuthorizationCodeStore
{
    /// <param name="codeChallenge">S256 PKCE challenge supplied by the app when it started sign-in.</param>
    string Create(string userId, string codeChallenge);

    bool TryRedeem(string code, string? codeVerifier, out string userId);
}

/// <summary>
/// One-time authorization codes for the mobile sign-in flow.
/// </summary>
/// <remarks>
/// The code is delivered to the app through the custom scheme <c>glosify://auth</c>, and any
/// other application on the device can register that scheme. Without PKCE, an app that wins
/// the race to handle the redirect could take the code straight to <c>/api/auth/external/exchange</c>
/// and receive real bearer tokens. Binding the code to a challenge the legitimate app kept to
/// itself makes an intercepted code useless.
/// </remarks>
public sealed class MobileAuthorizationCodeStore : IMobileAuthorizationCodeStore
{
    private const string CacheKeyPrefix = "mobile-auth-code:";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private readonly IMemoryCache _cache;
    private readonly TimeProvider _timeProvider;

    public MobileAuthorizationCodeStore(IMemoryCache cache, TimeProvider timeProvider)
    {
        _cache = cache;
        _timeProvider = timeProvider;
    }

    public string Create(string userId, string codeChallenge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (!Pkce.IsValidChallenge(codeChallenge))
        {
            throw new ArgumentException("A valid S256 PKCE code challenge is required.", nameof(codeChallenge));
        }

        // 256 bits from a CSPRNG, matching the extension flow. The previous implementation
        // concatenated two GUIDs, which is fine in practice but inconsistent with the rest
        // of this codebase's approach to secrets.
        var code = Pkce.CreateAuthorizationCode();
        var entry = new AuthorizationCodeEntry(userId, codeChallenge, _timeProvider.GetUtcNow().Add(Lifetime));
        _cache.Set(CacheKeyPrefix + code, entry, Lifetime);
        return code;
    }

    public bool TryRedeem(string code, string? codeVerifier, out string userId)
    {
        userId = string.Empty;
        if (string.IsNullOrWhiteSpace(code)
            || !_cache.TryGetValue(CacheKeyPrefix + code, out AuthorizationCodeEntry? entry)
            || entry is null)
        {
            return false;
        }

        // Consume before validating, so a wrong verifier cannot be retried against the
        // same code.
        _cache.Remove(CacheKeyPrefix + code);
        if (_timeProvider.GetUtcNow() > entry.ExpiresAt
            || !Pkce.Matches(entry.CodeChallenge, codeVerifier))
        {
            return false;
        }

        userId = entry.UserId;
        return true;
    }

    private sealed record AuthorizationCodeEntry(
        string UserId,
        string CodeChallenge,
        DateTimeOffset ExpiresAt);
}
