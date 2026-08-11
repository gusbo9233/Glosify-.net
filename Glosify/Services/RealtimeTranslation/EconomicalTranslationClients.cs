using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Azure.Core;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Extensions.Options;

namespace Glosify.Services.RealtimeTranslation;

public sealed record RecognizedSpeechSegment(
    int Sequence,
    string Text,
    string SourceLanguage,
    string SourceLocale,
    DateTimeOffset CapturedAt);

public interface IRealtimeSpeechTranscriber
{
    Task TranscribeAsync(
        string sourceLanguage,
        ChannelReader<byte[]> audio,
        ChannelWriter<RecognizedSpeechSegment> output,
        CancellationToken cancellationToken);
}

public interface IRealtimeTextTranslator
{
    Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken);
}

public sealed record TranslatedSubtitleSegment(
    int Sequence,
    string SourceText,
    string TranslatedText,
    string SourceLanguage,
    string TargetLanguage,
    DateTimeOffset CapturedAt);

public interface IEconomicalSubtitleTranslator
{
    Task<TranslatedSubtitleSegment> TranslateAsync(
        RecognizedSpeechSegment segment,
        string targetLanguage,
        CancellationToken cancellationToken);
}

public sealed class EconomicalSubtitleTranslator(IRealtimeTextTranslator translator)
    : IEconomicalSubtitleTranslator
{
    public async Task<TranslatedSubtitleSegment> TranslateAsync(
        RecognizedSpeechSegment segment,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var translated = string.Equals(
            segment.SourceLanguage,
            targetLanguage,
            StringComparison.OrdinalIgnoreCase)
            ? segment.Text
            : await translator.TranslateAsync(
                segment.Text,
                segment.SourceLanguage,
                targetLanguage,
                cancellationToken);
        return new TranslatedSubtitleSegment(
            segment.Sequence,
            segment.Text,
            translated,
            segment.SourceLanguage,
            targetLanguage,
            segment.CapturedAt);
    }
}

public sealed class AzureRealtimeSpeechTranscriber : IRealtimeSpeechTranscriber
{
    private readonly TokenCredential _credential;
    private readonly RealtimeTranslationOptions _options;
    private readonly TimeProvider _timeProvider;

    public AzureRealtimeSpeechTranscriber(
        TokenCredential credential,
        IOptions<RealtimeTranslationOptions> options,
        TimeProvider timeProvider)
    {
        _credential = credential;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public async Task TranscribeAsync(
        string sourceLanguage,
        ChannelReader<byte[]> audio,
        ChannelWriter<RecognizedSpeechSegment> output,
        CancellationToken cancellationToken)
    {
        if (!RealtimeTranslationOptionsValidator.TryValidateCognitiveEndpoint(
                _options.SpeechEndpoint,
                out var endpoint))
        {
            throw new RealtimeTranslationUnavailableException(
                "Economical speech transcription is not configured.");
        }

        var speechConfig = SpeechConfig.FromEndpoint(endpoint, _credential);
        speechConfig.OutputFormat = OutputFormat.Simple;
        using var format = AudioStreamFormat.GetWaveFormatPCM(24_000, 16, 1);
        using var pushStream = AudioInputStream.CreatePushStream(format);
        using var audioConfig = AudioConfig.FromStreamInput(pushStream);
        using var recognizer = CreateRecognizer(speechConfig, audioConfig, sourceLanguage);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;
        var sequence = 0;

        recognizer.Recognized += (_, eventArgs) =>
        {
            if (eventArgs.Result.Reason != ResultReason.RecognizedSpeech
                || string.IsNullOrWhiteSpace(eventArgs.Result.Text))
            {
                return;
            }
            var locale = ResolveLocale(eventArgs.Result, sourceLanguage);
            var code = ResolveLanguageCode(locale, sourceLanguage);
            if (!output.TryWrite(new RecognizedSpeechSegment(
                    Interlocked.Increment(ref sequence),
                    eventArgs.Result.Text.Trim(),
                    code,
                    locale,
                    _timeProvider.GetUtcNow())))
            {
                failure = new RealtimeTranslationUpstreamException(
                    "Economical subtitles could not keep up with the incoming speech.");
                stopped.TrySetResult();
            }
        };
        recognizer.Canceled += (_, eventArgs) =>
        {
            if (eventArgs.Reason == CancellationReason.Error)
            {
                failure = new RealtimeTranslationUpstreamException(
                    "Azure Speech ended economical transcription.");
            }
            stopped.TrySetResult();
        };
        recognizer.SessionStopped += (_, _) => stopped.TrySetResult();

        try
        {
            await recognizer.StartContinuousRecognitionAsync();
            await foreach (var bytes in audio.ReadAllAsync(cancellationToken))
            {
                pushStream.Write(bytes);
                if (failure is not null)
                {
                    throw failure;
                }
            }
            pushStream.Close();
            try
            {
                await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            }
            catch (TimeoutException)
            {
                // StopContinuousRecognitionAsync below performs the bounded final flush.
            }
            await recognizer.StopContinuousRecognitionAsync();
            if (failure is not null)
            {
                throw failure;
            }
        }
        finally
        {
            output.TryComplete(failure);
        }
    }

    private SpeechRecognizer CreateRecognizer(
        SpeechConfig speechConfig,
        AudioConfig audioConfig,
        string sourceLanguage)
    {
        if (sourceLanguage == "auto")
        {
            var locales = _options.SourceLanguages
                .Where(language => language.Enabled && language.AutoDetect)
                .Select(language => language.Locale)
                .ToArray();
            var autoDetect = AutoDetectSourceLanguageConfig.FromLanguages(locales);
            return new SpeechRecognizer(speechConfig, autoDetect, audioConfig);
        }

        var language = _options.FindSourceLanguage(sourceLanguage)
            ?? throw new RealtimeTranslationValidationException("Unsupported source language.");
        speechConfig.SpeechRecognitionLanguage = language.Locale;
        return new SpeechRecognizer(speechConfig, audioConfig);
    }

    private string ResolveLocale(SpeechRecognitionResult result, string requestedLanguage)
    {
        if (requestedLanguage != "auto")
        {
            return _options.FindSourceLanguage(requestedLanguage)?.Locale ?? requestedLanguage;
        }
        return AutoDetectSourceLanguageResult.FromResult(result).Language;
    }

    private string ResolveLanguageCode(string locale, string requestedLanguage) =>
        requestedLanguage != "auto"
            ? requestedLanguage
            : _options.SourceLanguages.FirstOrDefault(language =>
                string.Equals(language.Locale, locale, StringComparison.OrdinalIgnoreCase))?.Code
                ?? locale.Split('-', 2)[0].ToLowerInvariant();
}

public sealed class AzureRealtimeTextTranslator : IRealtimeTextTranslator
{
    public const string HttpClientName = "RealtimeTranslation.AzureTranslator";
    private const string CognitiveScope = "https://cognitiveservices.azure.com/.default";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TokenCredential _credential;
    private readonly RealtimeTranslationOptions _options;

    public AzureRealtimeTextTranslator(
        IHttpClientFactory httpClientFactory,
        TokenCredential credential,
        IOptions<RealtimeTranslationOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _credential = credential;
        _options = options.Value;
    }

    public async Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (!RealtimeTranslationOptionsValidator.TryValidateTranslatorEndpoint(
                _options.TranslatorEndpoint,
                out var endpoint,
                out var usesGlobalEndpoint))
        {
            throw new RealtimeTranslationUnavailableException(
                "Economical text translation is not configured.");
        }

        var sourceTranslatorCode = _options.FindSourceLanguage(sourceLanguage)?.TranslatorCode;
        var targetTranslatorCode = _options.FindLanguage(targetLanguage)?.TranslatorCode;
        if (string.IsNullOrWhiteSpace(sourceTranslatorCode)
            || string.IsNullOrWhiteSpace(targetTranslatorCode))
        {
            throw new RealtimeTranslationUnavailableException(
                "Economical subtitle language mapping is not configured.");
        }

        var requestUri = new Uri(
            endpoint,
            $"{(usesGlobalEndpoint ? string.Empty : "translator/text/v3.0/")}translate"
            + $"?api-version=3.0&from={Uri.EscapeDataString(sourceTranslatorCode)}"
            + $"&to={Uri.EscapeDataString(targetTranslatorCode)}");
        var token = await _credential.GetTokenAsync(
            new TokenRequestContext([CognitiveScope]),
            cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        if (usesGlobalEndpoint)
        {
            request.Headers.Add("Ocp-Apim-ResourceId", _options.TranslatorResourceId.Trim());
            if (!string.IsNullOrWhiteSpace(_options.TranslatorRegion))
            {
                request.Headers.Add("Ocp-Apim-Subscription-Region", _options.TranslatorRegion.Trim());
            }
        }
        request.Content = new StringContent(
            JsonSerializer.Serialize(new[] { new { Text = text } }),
            Encoding.UTF8,
            "application/json");
        using var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new RealtimeTranslationUpstreamException(
                "Azure Translator could not translate the current subtitle.");
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        var translated = document.RootElement[0].GetProperty("translations")[0].GetProperty("text").GetString();
        return !string.IsNullOrWhiteSpace(translated)
            ? translated
            : throw new RealtimeTranslationUpstreamException(
                "Azure Translator returned an empty subtitle.");
    }
}
