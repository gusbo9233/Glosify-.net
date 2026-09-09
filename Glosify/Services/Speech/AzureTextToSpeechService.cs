using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Glosify.Services.Storage;
using Glosify.Services.Ai;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Caching.Memory;

namespace Glosify.Services.Speech;

public sealed class AzureTextToSpeechService : ITextToSpeechService
{
    private const string DefaultOutputFormat = "audio-24khz-48kbitrate-mono-mp3";
    private const string PolishOutputFormat = "audio-48khz-192kbitrate-mono-mp3";
    private const string ContentType = "audio/mpeg";
    private static readonly TokenRequestContext SpeechTokenContext =
        new(["https://cognitiveservices.azure.com/.default"]);

    private readonly IMemoryCache _voiceCache;
    private readonly SpeechOptions _speech;
    private readonly BlobContainerClient? _container;
    private readonly TokenCredential _credential;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AzureTextToSpeechService> _logger;
    private readonly IPaidServiceGate _paidServices;

    public AzureTextToSpeechService(
        IOptions<SpeechOptions> speechOptions,
        GlosifyBlobServiceClient blobs,
        TokenCredential credential,
        IHttpClientFactory httpClientFactory,
        ILogger<AzureTextToSpeechService> logger,
        IPaidServiceGate paidServices,
        IMemoryCache voiceCache)
    {
        _voiceCache = voiceCache;
        _speech = speechOptions.Value;
        _credential = credential;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _paidServices = paidServices;
        _container = TryCreateContainer(blobs, _speech.BlobContainer, logger);
    }

    public bool IsConfigured =>
        StandardConnection is not null
        || HighDefinitionConnection is not null;

    private SpeechConnection? StandardConnection
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_speech.Endpoint)
                && !string.IsNullOrWhiteSpace(_speech.ResourceId))
            {
                return new SpeechConnection(
                    _speech.Endpoint,
                    _speech.ResourceId,
                    string.Empty,
                    _speech.Region);
            }

            if (!string.IsNullOrWhiteSpace(_speech.Key)
                && !string.IsNullOrWhiteSpace(_speech.Region))
            {
                return new SpeechConnection(
                    string.Empty,
                    string.Empty,
                    _speech.Key,
                    _speech.Region);
            }

            return null;
        }
    }

    private SpeechConnection? HighDefinitionConnection =>
        _speech.HighDefinition.Enabled
        && !string.IsNullOrWhiteSpace(_speech.HighDefinition.ResourceId)
        && (!string.IsNullOrWhiteSpace(_speech.HighDefinition.Endpoint)
            || !string.IsNullOrWhiteSpace(_speech.HighDefinition.Region))
            ? new SpeechConnection(
                _speech.HighDefinition.Endpoint,
                _speech.HighDefinition.ResourceId,
                string.Empty,
                _speech.HighDefinition.Region)
            : null;

    public async Task<Stream> GetOrSynthesizeAsync(
        string text,
        string languageCode,
        bool preferHighDefinition = false,
        string? voicePreference = null,
        CancellationToken cancellationToken = default)
    {
        await _paidServices.EnsureAvailableAsync(cancellationToken);
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Azure Speech is not configured.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Text is required.", nameof(text));
        }

        var locale = VoiceMap.ResolveLocale(languageCode)
            ?? throw new NotSupportedException("This language is not supported.");
        VoiceMap.TryResolve(languageCode, voicePreference, out _, out var voice);
        // Preserve existing short Polish preferences for older clients. New clients
        // submit the exact Azure voice id, validated against the resource's catalog.
        var legacyPolishVoice = locale == "pl-PL"
            && voicePreference?.Trim().ToLowerInvariant() is "zofia" or "agnieszka" or "marek";
        if ((!string.IsNullOrWhiteSpace(voicePreference) && !legacyPolishVoice)
            || string.IsNullOrEmpty(voice))
        {
            var choices = await GetVoicesAsync(languageCode, cancellationToken);
            var selected = string.IsNullOrWhiteSpace(voicePreference)
                ? choices.FirstOrDefault()
                : choices.FirstOrDefault(item => item.ShortName == voicePreference);
            if (selected is null)
                throw new NotSupportedException("The selected voice is not available for this language.");
            voice = selected.ShortName;
            locale = selected.Locale;
        }

        var trimmed = text.Trim();
        if (trimmed.Length > _speech.MaxTextLength)
        {
            trimmed = trimmed[.._speech.MaxTextLength];
        }

        var standardConnection = StandardConnection;
        var outputFormat = string.Equals(locale, "pl-PL", StringComparison.OrdinalIgnoreCase)
            ? PolishOutputFormat
            : DefaultOutputFormat;
        var highDefinitionConnection = preferHighDefinition && string.IsNullOrWhiteSpace(voicePreference)
            ? HighDefinitionConnection : null;
        if (highDefinitionConnection is not null
            && VoiceMap.TryResolveHighDefinition(languageCode, out var highDefinitionVoice))
        {
            try
            {
                return await GetOrSynthesizeWithVoiceAsync(
                    trimmed,
                    locale,
                    highDefinitionVoice,
                    outputFormat,
                    highDefinitionConnection,
                    cancellationToken);
            }
            catch (HttpRequestException ex) when (standardConnection is not null)
            {
                _logger.LogWarning(
                    ex,
                    "Dragon HD Omni synthesis failed; falling back to the standard voice.");
            }
        }

        var connection = standardConnection ?? HighDefinitionConnection
            ?? throw new InvalidOperationException("Azure Speech is not configured.");
        return await GetOrSynthesizeWithVoiceAsync(
            trimmed,
            locale,
            voice,
            outputFormat,
            connection,
            cancellationToken);
    }

    public async Task<IReadOnlyList<SpeechVoice>> GetVoicesAsync(
        string languageCode, CancellationToken cancellationToken = default)
    {
        var locale = VoiceMap.ResolveLocale(languageCode);
        if (locale is null) return [];
        var connection = StandardConnection ?? HighDefinitionConnection;
        if (connection is null) return [];
        var endpoint = BuildSynthesisEndpoint(connection);
        var cacheKey = $"speech-voices:{endpoint}:{connection.ResourceId}";
        var all = await _voiceCache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            var path = string.IsNullOrWhiteSpace(connection.Region)
                ? "/tts/cognitiveservices/voices/list" : "/cognitiveservices/voices/list";
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, path));
            await AuthorizeAsync(request, connection, cancellationToken);
            var client = _httpClientFactory.CreateClient(nameof(AzureTextToSpeechService));
            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<SpeechVoice[]>(cancellationToken)
                ?? [];
        });
        // Only expose standard neural voices in the requested locale. HD selection
        // has a separate resource and must not silently replace a chosen voice.
        return (all ?? []).Where(item =>
                string.Equals(item.Locale, locale, StringComparison.OrdinalIgnoreCase)
                && item.ShortName.StartsWith(locale + "-", StringComparison.OrdinalIgnoreCase)
                && item.ShortName.EndsWith("Neural", StringComparison.Ordinal)
                && item.ShortName.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
            .OrderBy(item => item.DisplayName, StringComparer.Ordinal).ToArray();
    }

    private async Task AuthorizeAsync(HttpRequestMessage request, SpeechConnection connection, CancellationToken ct)
    {
        if (connection.UsesEntra)
        {
            var token = await _credential.GetTokenAsync(SpeechTokenContext, ct);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", $"aad#{connection.ResourceId.Trim()}#{token.Token}");
        }
        else
        {
            request.Headers.Add("Ocp-Apim-Subscription-Key", connection.Key);
        }
    }

    private async Task<Stream> GetOrSynthesizeWithVoiceAsync(
        string text,
        string locale,
        string voice,
        string outputFormat,
        SpeechConnection connection,
        CancellationToken cancellationToken)
    {
        var blobName = BuildBlobName(voice, outputFormat, text);

        if (_container is not null)
        {
            var cached = await TryOpenCachedAsync(blobName, cancellationToken);
            if (cached is not null)
            {
                return cached;
            }
        }

        var audio = await SynthesizeAsync(
            text,
            locale,
            voice,
            outputFormat,
            connection,
            cancellationToken);

        if (_container is not null)
        {
            await TryCacheAsync(blobName, audio, cancellationToken);
            audio.Position = 0;
        }

        return audio;
    }

    private async Task<byte[]> SynthesizeBytesAsync(
        string text,
        string locale,
        string voice,
        string outputFormat,
        SpeechConnection connection,
        CancellationToken ct)
    {
        var ssml = BuildSsml(text, locale, voice);
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildSynthesisEndpoint(connection));
        await AuthorizeAsync(request, connection, ct);
        request.Headers.Add("X-Microsoft-OutputFormat", outputFormat);
        request.Headers.Add("User-Agent", "Glosify");
        request.Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml");

        var client = _httpClientFactory.CreateClient(nameof(AzureTextToSpeechService));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Azure Speech synthesis failed with {StatusCode}.",
                response.StatusCode);
            throw new HttpRequestException($"Speech synthesis failed with status {(int)response.StatusCode}.");
        }

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private static Uri BuildSynthesisEndpoint(SpeechConnection connection)
    {
        // Azure's Entra-enabled custom domain identifies the resource, but the
        // synthesis REST route is hosted on the regional TTS endpoint.
        if (!string.IsNullOrWhiteSpace(connection.Region))
        {
            return new Uri(
                $"https://{connection.Region.Trim()}.tts.speech.microsoft.com/cognitiveservices/v1",
                UriKind.Absolute);
        }

        if (!string.IsNullOrWhiteSpace(connection.Endpoint))
        {
            if (!Uri.TryCreate(connection.Endpoint.Trim(), UriKind.Absolute, out var resourceEndpoint)
                || resourceEndpoint.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException("Speech endpoint must be an absolute HTTPS URI.");
            }

            return new Uri(
                $"{resourceEndpoint.AbsoluteUri.TrimEnd('/')}/cognitiveservices/v1",
                UriKind.Absolute);
        }

        throw new InvalidOperationException("Speech region or endpoint must be configured.");
    }

    private async Task<MemoryStream> SynthesizeAsync(
        string text,
        string locale,
        string voice,
        string outputFormat,
        SpeechConnection connection,
        CancellationToken ct)
    {
        var bytes = await SynthesizeBytesAsync(text, locale, voice, outputFormat, connection, ct);
        return new MemoryStream(bytes, writable: false);
    }

    private async Task<Stream?> TryOpenCachedAsync(string blobName, CancellationToken ct)
    {
        try
        {
            var blob = _container!.GetBlobClient(blobName);
            var response = await blob.DownloadStreamingAsync(cancellationToken: ct);
            return response.Value.Content;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (RequestFailedException ex)
        {
            _logger.LogWarning(ex, "TTS cache read failed for {Blob}.", blobName);
            return null;
        }
    }

    private async Task TryCacheAsync(string blobName, MemoryStream audio, CancellationToken ct)
    {
        try
        {
            await _container!.CreateIfNotExistsAsync(cancellationToken: ct);
            var blob = _container.GetBlobClient(blobName);
            audio.Position = 0;
            await blob.UploadAsync(
                audio,
                new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = ContentType } },
                ct);
        }
        catch (RequestFailedException ex)
        {
            _logger.LogWarning(ex, "TTS cache write failed for {Blob}.", blobName);
        }
    }

    private static string BuildBlobName(string voice, string outputFormat, string text)
    {
        var bytes = Encoding.UTF8.GetBytes($"{voice}|{outputFormat}|{text}");
        var hash = SHA256.HashData(bytes);
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return $"{voice}/{hex}.mp3";
    }

    private static string BuildSsml(string text, string locale, string voice)
    {
        var escaped = new StringBuilder();
        using (var writer = XmlWriter.Create(escaped, new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            ConformanceLevel = ConformanceLevel.Fragment,
        }))
        {
            writer.WriteString(text);
        }

        return $"<speak version='1.0' xml:lang='{locale}'>" +
               $"<voice xml:lang='{locale}' name='{voice}'>{escaped}</voice>" +
               "</speak>";
    }

    private static BlobContainerClient? TryCreateContainer(
        GlosifyBlobServiceClient blobs,
        string containerName,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(containerName))
        {
            return null;
        }

        try
        {
            var container = blobs.GetContainerOrDefault(containerName);
            if (container is null)
            {
                logger.LogInformation("TTS blob cache disabled: BlobStorage not configured.");
                return null;
            }
            return container;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to initialize TTS blob cache; synthesis will bypass cache.");
            return null;
        }
    }

    private sealed record SpeechConnection(
        string Endpoint,
        string ResourceId,
        string Key,
        string Region)
    {
        public bool UsesEntra => !string.IsNullOrWhiteSpace(ResourceId);
    }
}
