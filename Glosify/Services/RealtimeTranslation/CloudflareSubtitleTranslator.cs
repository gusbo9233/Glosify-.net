using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Glosify.Services.RealtimeTranslation;

public interface ICloudflareSubtitleTranslator
{
    Task<TranslatedSubtitleSegment> TranslateAsync(
        RecognizedSpeechSegment segment,
        string targetLanguage,
        CancellationToken cancellationToken);
}

public sealed class CloudflareSubtitleTranslator : ICloudflareSubtitleTranslator
{
    public const string HttpClientName = "RealtimeTranslation.CloudflareTranslator";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly RealtimeTranslationOptions _options;

    public CloudflareSubtitleTranslator(
        IHttpClientFactory httpClientFactory,
        IOptions<RealtimeTranslationOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    public async Task<TranslatedSubtitleSegment> TranslateAsync(
        RecognizedSpeechSegment segment,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var sourceCode = string.Equals(
            segment.SourceLanguage,
            "auto",
            StringComparison.OrdinalIgnoreCase)
                ? null
                : NormalizeLanguageCode(segment.SourceLanguage);
        var targetCode = NormalizeLanguageCode(targetLanguage);
        if (sourceCode is null)
        {
            throw new RealtimeTranslationUpstreamException(
                "Cloudflare translation is waiting for Scribe to detect the spoken language.");
        }
        var sameLanguage = sourceCode is not null
            && string.Equals(sourceCode, targetCode, StringComparison.OrdinalIgnoreCase);
        var translated = sameLanguage
            ? segment.Text
            : await TranslateChunksAsync(segment.Text, sourceCode, targetCode, cancellationToken);

        return new TranslatedSubtitleSegment(
            segment.Sequence,
            segment.Text,
            translated,
            segment.SourceLanguage,
            targetLanguage,
            segment.CapturedAt,
            ProviderRequest: !sameLanguage);
    }

    private async Task<string> TranslateChunksAsync(
        string text,
        string? sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (!_options.Cloudflare.Enabled
            || !RealtimeTranslationOptionsValidator.IsValidCloudflareWorkerEndpoint(
                _options.Cloudflare.Endpoint,
                out var endpoint)
            || string.IsNullOrWhiteSpace(_options.Cloudflare.ApiToken))
        {
            throw new RealtimeTranslationUnavailableException(
                "Cloudflare subtitle translation is not configured.");
        }

        var chunks = SplitIntoSentenceBoundedChunks(
            text,
            _options.Cloudflare.PreferredChunkCharacters,
            _options.Cloudflare.MaxInputCharacters);
        var translations = new List<string>(chunks.Count);
        for (var offset = 0; offset < chunks.Count; offset += _options.Cloudflare.MaxParallelRequests)
        {
            var batch = chunks
                .Skip(offset)
                .Take(_options.Cloudflare.MaxParallelRequests)
                .Select(chunk => TranslateChunkAsync(
                    endpoint,
                    chunk,
                    sourceLanguage,
                    targetLanguage,
                    cancellationToken));
            translations.AddRange(await Task.WhenAll(batch));
        }
        return string.Join(' ', translations);
    }

    private async Task<string> TranslateChunkAsync(
        Uri endpoint,
        string text,
        string? sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _options.Cloudflare.ApiToken.Trim());
        request.Content = new StringContent(
            JsonSerializer.Serialize(
                new CloudflareTranslationRequest(text, sourceLanguage, targetLanguage),
                SerializerOptions),
            Encoding.UTF8,
            "application/json");

        using var client = _httpClientFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RealtimeTranslationUpstreamException(
                "Cloudflare timed out while translating the current subtitle.");
        }
        catch (TimeoutException)
        {
            throw new RealtimeTranslationUpstreamException(
                "Cloudflare timed out while translating the current subtitle.");
        }
        catch (HttpRequestException)
        {
            throw new RealtimeTranslationUpstreamException(
                "Cloudflare could not be reached for subtitle translation.");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new RealtimeTranslationUpstreamException(
                    $"Cloudflare could not translate the current subtitle (HTTP {(int)response.StatusCode}).");
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            CloudflareTranslationResponse? result;
            try
            {
                result = await JsonSerializer.DeserializeAsync<CloudflareTranslationResponse>(
                    body,
                    SerializerOptions,
                    cancellationToken);
            }
            catch (JsonException)
            {
                throw new RealtimeTranslationUpstreamException(
                    "Cloudflare returned an invalid translation response.");
            }
            if (string.IsNullOrWhiteSpace(result?.Translated))
            {
                throw new RealtimeTranslationUpstreamException(
                    "Cloudflare returned an empty translation response.");
            }
            return result.Translated.Trim();
        }
    }

    internal static IReadOnlyList<string> SplitIntoBoundedChunks(string text, int maximumCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCharacters, 1);
        var remaining = text.Trim();
        var chunks = new List<string>();
        while (remaining.Length > maximumCharacters)
        {
            var limit = maximumCharacters;
            var splitAt = remaining.LastIndexOfAny(['.', '!', '?', '…', '。', '！', '？'], limit - 1, limit);
            if (splitAt < maximumCharacters / 2)
            {
                splitAt = remaining.LastIndexOf(' ', limit - 1, limit);
            }
            if (splitAt < 1)
            {
                splitAt = limit - 1;
            }
            else if (!char.IsWhiteSpace(remaining[splitAt]))
            {
                splitAt++;
            }
            chunks.Add(remaining[..splitAt].Trim());
            remaining = remaining[splitAt..].TrimStart();
        }
        if (remaining.Length > 0)
        {
            chunks.Add(remaining);
        }
        return chunks;
    }

    internal static IReadOnlyList<string> SplitIntoSentenceBoundedChunks(
        string text,
        int preferredCharacters,
        int maximumCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(preferredCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCharacters, preferredCharacters);
        var split = SubtitleSentenceSegmenter.Split(text);
        var units = split.Completed
            .Append(split.Remainder)
            .Where(value => value.Length > 0);
        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var unit in units)
        {
            if (unit.Length > maximumCharacters)
            {
                FlushCurrent();
                chunks.AddRange(SplitIntoBoundedChunks(unit, maximumCharacters));
                continue;
            }

            var combinedLength = current.Length + (current.Length > 0 ? 1 : 0) + unit.Length;
            if (current.Length > 0 && combinedLength > preferredCharacters)
            {
                FlushCurrent();
            }
            if (current.Length > 0)
            {
                current.Append(' ');
            }
            current.Append(unit);
        }

        FlushCurrent();
        return chunks;

        void FlushCurrent()
        {
            if (current.Length == 0)
            {
                return;
            }
            chunks.Add(current.ToString());
            current.Clear();
        }
    }

    private static string NormalizeLanguageCode(string value)
    {
        var code = value.Trim().ToLowerInvariant().Split('-', 2)[0];
        return code switch
        {
            "yue" => "zh",
            "fil" => "tl",
            "nb" => "no",
            var normalized => normalized,
        };
    }

    private sealed record CloudflareTranslationRequest(
        string Text,
        [property: JsonPropertyName("source_lang")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? SourceLanguage,
        [property: JsonPropertyName("target_lang")] string TargetLanguage);

    private sealed record CloudflareTranslationResponse(string Translated);
}
