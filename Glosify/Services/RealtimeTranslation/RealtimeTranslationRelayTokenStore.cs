using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Glosify.Services.RealtimeTranslation;

public interface IRealtimeTranslationRelayTokenStore
{
    RealtimeTranslationRelayGrant Create(Guid sessionId, string userId, string targetLanguage, bool saveTranscript);

    bool TryRedeem(
        Guid sessionId,
        string token,
        out RealtimeTranslationRelayAuthorization authorization);
}

public sealed class RealtimeTranslationRelayTokenStore : IRealtimeTranslationRelayTokenStore
{
    private const string CacheKeyPrefix = "realtime-translation-relay:";

    private readonly IMemoryCache _cache;
    private readonly RealtimeTranslationOptions _options;
    private readonly TimeProvider _timeProvider;

    public RealtimeTranslationRelayTokenStore(
        IMemoryCache cache,
        IOptions<RealtimeTranslationOptions> options,
        TimeProvider timeProvider)
    {
        _cache = cache;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public RealtimeTranslationRelayGrant Create(
        Guid sessionId,
        string userId,
        string targetLanguage,
        bool saveTranscript)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);

        var lifetime = TimeSpan.FromSeconds(Math.Clamp(
            _options.RelayTokenLifetimeSeconds,
            30,
            300));
        var expiresAt = _timeProvider.GetUtcNow().Add(lifetime);
        var token = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var entry = new RelayTokenEntry(sessionId, userId, targetLanguage, saveTranscript, expiresAt);
        _cache.Set(CacheKeyPrefix + HashToken(token), entry, lifetime);
        return new RealtimeTranslationRelayGrant(token, expiresAt);
    }

    public bool TryRedeem(
        Guid sessionId,
        string token,
        out RealtimeTranslationRelayAuthorization authorization)
    {
        authorization = default!;
        if (!IsValidToken(token))
        {
            return false;
        }

        var cacheKey = CacheKeyPrefix + HashToken(token);
        if (!_cache.TryGetValue(cacheKey, out RelayTokenEntry? entry) || entry is null)
        {
            return false;
        }

        // Relay grants are single-use even when the caller supplies the wrong
        // session id. This prevents retrying a captured token against routes.
        _cache.Remove(cacheKey);
        if (entry.SessionId != sessionId || _timeProvider.GetUtcNow() > entry.ExpiresAt)
        {
            return false;
        }

        authorization = new RealtimeTranslationRelayAuthorization(
            entry.SessionId,
            entry.UserId,
            entry.TargetLanguage,
            entry.SaveTranscript);
        return true;
    }

    internal static bool IsValidToken(string? token) =>
        token is { Length: 43 }
        && token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token)));

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed record RelayTokenEntry(
        Guid SessionId,
        string UserId,
        string TargetLanguage,
        bool SaveTranscript,
        DateTimeOffset ExpiresAt);
}
