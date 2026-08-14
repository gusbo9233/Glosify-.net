using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Glosify.Services.RealtimeTranslation;

public interface IRealtimeTranslationLanguageCatalog
{
    Task<IReadOnlyList<RealtimeTranslationLanguage>> GetLanguagesAsync(
        CancellationToken cancellationToken = default);
}

public sealed class AzureTranslatorLanguageCatalog : IRealtimeTranslationLanguageCatalog
{
    internal const string HttpClientName = "RealtimeTranslation.AzureTranslatorLanguages";
    private const string CacheKey = "realtime-translation:translator-languages:v1";
    private const string LastKnownCacheKey = "realtime-translation:translator-languages:last-known:v1";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly RealtimeTranslationOptions _options;
    private readonly ILogger<AzureTranslatorLanguageCatalog> _logger;

    public AzureTranslatorLanguageCatalog(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IOptions<RealtimeTranslationOptions> options,
        ILogger<AzureTranslatorLanguageCatalog> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RealtimeTranslationLanguage>> GetLanguagesAsync(
        CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(CacheKey, out IReadOnlyList<RealtimeTranslationLanguage>? cached)
            && cached is { Count: > 0 })
        {
            return cached;
        }

        try
        {
            using var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "languages?api-version=3.0&scope=translation");
            request.Headers.AcceptLanguage.ParseAdd("en");
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("translation", out var translations)
                || translations.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Azure Translator language response is missing translation languages.");
            }

            var languages = translations.EnumerateObject()
                .Select(item => new RealtimeTranslationLanguage(
                    item.Name,
                    item.Value.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                        ? name.GetString() ?? item.Name
                        : item.Name))
                .OrderBy(language => language.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(language => language.Code, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (languages.Length == 0)
            {
                throw new JsonException("Azure Translator returned no translation languages.");
            }

            _cache.Set(CacheKey, languages, CacheLifetime);
            _cache.Set(LastKnownCacheKey, languages);
            return languages;
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested
            && exception is (HttpRequestException or JsonException or TaskCanceledException))
        {
            if (_cache.TryGetValue(
                    LastKnownCacheKey,
                    out IReadOnlyList<RealtimeTranslationLanguage>? lastKnown)
                && lastKnown is { Count: > 0 })
            {
                _logger.LogWarning(
                    exception,
                    "Could not refresh Azure Translator languages; using the last known catalog.");
                return lastKnown;
            }
            var fallback = _options.Languages
                .Where(language => language.Enabled)
                .Select(language => new RealtimeTranslationLanguage(language.Code, language.Name))
                .OrderBy(language => language.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _logger.LogWarning(
                exception,
                "Could not refresh Azure Translator languages; using the configured fallback catalog.");
            return fallback;
        }
    }
}
