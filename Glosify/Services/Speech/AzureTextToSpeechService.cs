using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Glosify.Services.Storage;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Speech;

public sealed class AzureTextToSpeechService : ITextToSpeechService
{
    private const string OutputFormat = "audio-24khz-48kbitrate-mono-mp3";
    private const string ContentType = "audio/mpeg";
    private static readonly TokenRequestContext SpeechTokenContext =
        new(["https://cognitiveservices.azure.com/.default"]);

    private readonly SpeechOptions _speech;
    private readonly BlobContainerClient? _container;
    private readonly TokenCredential _credential;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AzureTextToSpeechService> _logger;

    public AzureTextToSpeechService(
        IOptions<SpeechOptions> speechOptions,
        IOptions<BlobStorageOptions> blobOptions,
        TokenCredential credential,
        IHttpClientFactory httpClientFactory,
        ILogger<AzureTextToSpeechService> logger)
    {
        _speech = speechOptions.Value;
        _credential = credential;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _container = TryCreateContainer(blobOptions.Value, _speech.BlobContainer, credential, logger);
    }

    public bool IsConfigured =>
        HasEntraConfiguration
        || HasKeyConfiguration;

    private bool HasEntraConfiguration =>
        !string.IsNullOrWhiteSpace(_speech.Endpoint)
        && !string.IsNullOrWhiteSpace(_speech.ResourceId);

    private bool HasKeyConfiguration =>
        !string.IsNullOrWhiteSpace(_speech.Key)
        && !string.IsNullOrWhiteSpace(_speech.Region);

    public async Task<Stream> GetOrSynthesizeAsync(
        string text,
        string languageCode,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Azure Speech is not configured.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Text is required.", nameof(text));
        }

        if (!VoiceMap.TryResolve(languageCode, out var locale, out var voice))
        {
            throw new NotSupportedException($"Language '{languageCode}' has no configured voice.");
        }

        var trimmed = text.Trim();
        if (trimmed.Length > _speech.MaxTextLength)
        {
            trimmed = trimmed[.._speech.MaxTextLength];
        }

        var blobName = BuildBlobName(voice, trimmed);

        if (_container is not null)
        {
            var cached = await TryOpenCachedAsync(blobName, cancellationToken);
            if (cached is not null)
            {
                return cached;
            }
        }

        var audio = await SynthesizeAsync(trimmed, locale, voice, cancellationToken);

        if (_container is not null)
        {
            await TryCacheAsync(blobName, audio, cancellationToken);
            audio.Position = 0;
        }

        return audio;
    }

    private async Task<byte[]> SynthesizeBytesAsync(string text, string locale, string voice, CancellationToken ct)
    {
        var ssml = BuildSsml(text, locale, voice);
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildSynthesisEndpoint());
        if (HasEntraConfiguration)
        {
            var token = await _credential.GetTokenAsync(SpeechTokenContext, ct);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                $"aad#{_speech.ResourceId.Trim()}#{token.Token}");
        }
        else
        {
            request.Headers.Add("Ocp-Apim-Subscription-Key", _speech.Key);
        }
        request.Headers.Add("X-Microsoft-OutputFormat", OutputFormat);
        request.Headers.Add("User-Agent", "Glosify");
        request.Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml");

        var client = _httpClientFactory.CreateClient(nameof(AzureTextToSpeechService));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "Azure Speech synthesis failed with {StatusCode}: {Body}",
                response.StatusCode,
                body);
            throw new HttpRequestException($"Speech synthesis failed with status {(int)response.StatusCode}.");
        }

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private Uri BuildSynthesisEndpoint()
    {
        if (!HasEntraConfiguration)
        {
            return new Uri(
                $"https://{_speech.Region.Trim()}.tts.speech.microsoft.com/cognitiveservices/v1",
                UriKind.Absolute);
        }

        if (!Uri.TryCreate(_speech.Endpoint.Trim(), UriKind.Absolute, out var resourceEndpoint)
            || resourceEndpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Speech:Endpoint must be an absolute HTTPS URI.");
        }

        return new Uri(
            $"{resourceEndpoint.AbsoluteUri.TrimEnd('/')}/cognitiveservices/v1",
            UriKind.Absolute);
    }

    private async Task<MemoryStream> SynthesizeAsync(string text, string locale, string voice, CancellationToken ct)
    {
        var bytes = await SynthesizeBytesAsync(text, locale, voice, ct);
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

    private static string BuildBlobName(string voice, string text)
    {
        var bytes = Encoding.UTF8.GetBytes($"{voice}|{text}");
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
        BlobStorageOptions options,
        string containerName,
        TokenCredential credential,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(containerName))
        {
            return null;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(options.ConnectionString))
            {
                return new BlobContainerClient(options.ConnectionString, containerName);
            }

            var serviceUri = !string.IsNullOrWhiteSpace(options.ServiceUri)
                ? options.ServiceUri
                : !string.IsNullOrWhiteSpace(options.AccountName)
                    ? $"https://{options.AccountName}.blob.core.windows.net"
                    : null;

            if (serviceUri is null)
            {
                logger.LogInformation("TTS blob cache disabled: BlobStorage not configured.");
                return null;
            }

            var service = new BlobServiceClient(new Uri(serviceUri, UriKind.Absolute), credential);
            return service.GetBlobContainerClient(containerName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to initialize TTS blob cache; synthesis will bypass cache.");
            return null;
        }
    }
}
