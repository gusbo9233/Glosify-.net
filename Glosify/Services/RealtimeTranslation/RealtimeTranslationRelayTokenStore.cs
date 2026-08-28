using System.Security.Cryptography;
using Glosify.Services.Language;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Glosify.Services.RealtimeTranslation;

public interface IRealtimeTranslationRelayTokenStore
{
    RealtimeTranslationRelayGrant Create(
        Guid sessionId,
        string userId,
        string targetLanguage,
        string translationMode,
        string speechProvider,
        string? sourceLanguage,
        bool saveTranscript,
        string? transcriptSourceLanguage,
        bool partialCaptionsEnabled = true);

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
        string translationMode,
        string speechProvider,
        string? sourceLanguage,
        bool saveTranscript,
        string? transcriptSourceLanguage,
        bool partialCaptionsEnabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);

        if (translationMode is not (
                RealtimeTranslationModes.Scribe
                or RealtimeTranslationModes.ScribeCloudflare
                or RealtimeTranslationModes.Enhanced))
        {
            throw new ArgumentException("Unsupported subtitle mode.", nameof(translationMode));
        }
        if (speechProvider is not (
                RealtimeSpeechProviders.Azure
                or RealtimeSpeechProviders.ElevenLabs
                or RealtimeSpeechProviders.OpenAi))
        {
            throw new ArgumentException("Unsupported speech provider.", nameof(speechProvider));
        }
        var expectedSpeechProvider = translationMode switch
        {
            RealtimeTranslationModes.Scribe or RealtimeTranslationModes.ScribeCloudflare =>
                RealtimeSpeechProviders.ElevenLabs,
            _ => RealtimeSpeechProviders.OpenAi,
        };
        if (!string.Equals(speechProvider, expectedSpeechProvider, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The speech provider does not match the authorized subtitle mode.",
                nameof(speechProvider));
        }
        var canonicalTranscriptSourceLanguage = saveTranscript
            ? QuizLanguageCatalog.Find(transcriptSourceLanguage)?.Code
                ?? throw new ArgumentException(
                    "Saved source transcription requires a supported quiz language.",
                    nameof(transcriptSourceLanguage))
            : null;

        var lifetime = TimeSpan.FromSeconds(Math.Clamp(
            _options.RelayTokenLifetimeSeconds,
            30,
            300));
        var expiresAt = _timeProvider.GetUtcNow().Add(lifetime);
        var token = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var entry = new RelayTokenEntry(
            sessionId,
            userId,
            targetLanguage,
            translationMode,
            speechProvider,
            sourceLanguage,
            saveTranscript,
            canonicalTranscriptSourceLanguage,
            partialCaptionsEnabled,
            expiresAt);
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
            entry.TranslationMode,
            entry.SpeechProvider,
            entry.SourceLanguage,
            entry.SaveTranscript,
            entry.TranscriptSourceLanguage,
            entry.PartialCaptionsEnabled);
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
        string TranslationMode,
        string SpeechProvider,
        string? SourceLanguage,
        bool SaveTranscript,
        string? TranscriptSourceLanguage,
        bool PartialCaptionsEnabled,
        DateTimeOffset ExpiresAt);
}
