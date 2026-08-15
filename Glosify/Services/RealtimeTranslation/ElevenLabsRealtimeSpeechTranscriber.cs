using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Glosify.Services.Language;
using Microsoft.Extensions.Options;

namespace Glosify.Services.RealtimeTranslation;

public sealed class RealtimeSpeechTranscriberRouter(
    AzureRealtimeSpeechTranscriber azure,
    ElevenLabsRealtimeSpeechTranscriber elevenLabs) : IRealtimeSpeechTranscriber
{
    public Task TranscribeAsync(
        string speechProvider,
        string sourceLanguage,
        ChannelReader<byte[]> audio,
        ChannelWriter<RecognizedSpeechSegment> output,
        bool emitPartials,
        CancellationToken cancellationToken) =>
        speechProvider switch
        {
            RealtimeSpeechProviders.Azure => azure.TranscribeAsync(
                speechProvider,
                sourceLanguage,
                audio,
                output,
                emitPartials,
                cancellationToken),
            RealtimeSpeechProviders.ElevenLabs => elevenLabs.TranscribeAsync(
                speechProvider,
                sourceLanguage,
                audio,
                output,
                emitPartials,
                cancellationToken),
            _ => throw new RealtimeTranslationValidationException(
                "The requested speech provider is not supported."),
        };
}

public interface IElevenLabsRealtimeWebSocketFactory
{
    Task<WebSocket> ConnectAsync(
        Uri endpoint,
        string apiKey,
        CancellationToken cancellationToken);
}

public sealed class ElevenLabsRealtimeWebSocketFactory : IElevenLabsRealtimeWebSocketFactory
{
    public async Task<WebSocket> ConnectAsync(
        Uri endpoint,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("xi-api-key", apiKey);
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

public sealed class ElevenLabsRealtimeSpeechTranscriber : IRealtimeSpeechTranscriber
{
    internal const int AudioSampleRate = 24_000;
    private const int MaximumProviderMessageBytes = 256 * 1024;
    private static readonly TimeSpan FinalCommitTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PartialEmissionInterval = TimeSpan.FromMilliseconds(750);

    private readonly IElevenLabsRealtimeWebSocketFactory _socketFactory;
    private readonly RealtimeTranslationOptions _options;
    private readonly TimeProvider _timeProvider;

    public ElevenLabsRealtimeSpeechTranscriber(
        IElevenLabsRealtimeWebSocketFactory socketFactory,
        IOptions<RealtimeTranslationOptions> options,
        TimeProvider timeProvider)
    {
        _socketFactory = socketFactory;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public async Task TranscribeAsync(
        string speechProvider,
        string sourceLanguage,
        ChannelReader<byte[]> audio,
        ChannelWriter<RecognizedSpeechSegment> output,
        bool emitPartials,
        CancellationToken cancellationToken)
    {
        if (speechProvider != RealtimeSpeechProviders.ElevenLabs)
        {
            throw new RealtimeTranslationValidationException(
                "ElevenLabs Scribe received an unsupported speech provider.");
        }
        if (!_options.ElevenLabs.Enabled
            || string.IsNullOrWhiteSpace(_options.ElevenLabs.ApiKey)
            || !RealtimeTranslationOptionsValidator.TryValidateElevenLabsEndpoint(
                _options.ElevenLabs.Endpoint,
                out _))
        {
            throw new RealtimeTranslationUnavailableException(
                "ElevenLabs Scribe v2 is not configured.");
        }

        var endpoint = BuildEndpoint(sourceLanguage);
        using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        WebSocket? socket = null;
        Exception? failure = null;
        Task? receiveTask = null;
        Task? sendTask = null;
        try
        {
            socket = await _socketFactory.ConnectAsync(
                endpoint,
                _options.ElevenLabs.ApiKey.Trim(),
                relayCancellation.Token);
            var finalCommit = new FinalCommitTracker();
            receiveTask = ReceiveTranscriptsAsync(
                socket,
                sourceLanguage,
                output,
                finalCommit,
                emitPartials,
                relayCancellation.Token);
            sendTask = SendAudioAsync(socket, audio, relayCancellation.Token);

            var completed = await Task.WhenAny(sendTask, receiveTask);
            if (completed == receiveTask)
            {
                await receiveTask;
                throw new RealtimeTranslationUpstreamException(
                    "ElevenLabs Scribe v2 ended unexpectedly.");
            }

            await sendTask;
            finalCommit.BeginWaiting();
            await SendJsonAsync(socket, new
            {
                message_type = "input_audio_chunk",
                audio_base_64 = string.Empty,
                commit = true,
                sample_rate = AudioSampleRate,
            }, relayCancellation.Token);
            try
            {
                await finalCommit.WaitAsync(FinalCommitTimeout, relayCancellation.Token);
            }
            catch (TimeoutException)
            {
                // Silence may leave nothing to commit. Close normally after a bounded flush.
            }

            await CloseOutputQuietlyAsync(socket);
            try
            {
                await receiveTask.WaitAsync(FinalCommitTimeout, relayCancellation.Token);
            }
            catch (TimeoutException)
            {
                relayCancellation.Cancel();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Browser disconnect, application shutdown, or session timeout.
        }
        catch (RealtimeTranslationValidationException)
        {
            throw;
        }
        catch (RealtimeTranslationUnavailableException)
        {
            throw;
        }
        catch (RealtimeTranslationUpstreamException exception)
        {
            failure = exception;
            throw;
        }
        catch (Exception exception) when (exception is WebSocketException or JsonException)
        {
            failure = new RealtimeTranslationUpstreamException(
                "ElevenLabs Scribe v2 ended the transcription stream.");
            throw failure;
        }
        finally
        {
            relayCancellation.Cancel();
            await DrainProviderTasksQuietlyAsync(sendTask, receiveTask);
            if (socket is not null)
            {
                await CloseOutputQuietlyAsync(socket);
                socket.Dispose();
            }
            output.TryComplete(failure);
        }
    }

    private static async Task DrainProviderTasksQuietlyAsync(params Task?[] tasks)
    {
        foreach (var task in tasks)
        {
            if (task is null)
            {
                continue;
            }
            try
            {
                await task;
            }
            catch (Exception exception) when (exception
                is OperationCanceledException
                or WebSocketException
                or ObjectDisposedException
                or RealtimeTranslationUpstreamException
                or JsonException
                or ChannelClosedException)
            {
                // The selected task already reported the provider outcome. Draining the
                // other task prevents disposal races and observes its teardown failure.
            }
        }
    }

    internal Uri BuildEndpoint(string sourceLanguage)
    {
        var settings = _options.ElevenLabs;
        var query = new List<string>
        {
            Pair("model_id", settings.Model.Trim()),
            Pair("audio_format", "pcm_24000"),
            Pair("commit_strategy", "vad"),
            Pair("vad_silence_threshold_secs", settings.VadSilenceThresholdSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture)),
            Pair("vad_threshold", settings.VadThreshold.ToString(
                System.Globalization.CultureInfo.InvariantCulture)),
            Pair("include_timestamps", "true"),
            Pair("include_language_detection", "true"),
            Pair("enable_logging", settings.EnableLogging ? "true" : "false"),
            Pair("no_verbatim", "false"),
        };

        if (sourceLanguage != "auto")
        {
            var language = _options.FindSourceLanguage(sourceLanguage);
            var catalogLanguage = QuizLanguageCatalog.Find(sourceLanguage);
            query.Add(Pair(
                "language_code",
                !string.IsNullOrWhiteSpace(language?.ScribeCode)
                    ? language.ScribeCode
                    : catalogLanguage?.ScribeCode ?? sourceLanguage.Trim().ToLowerInvariant()));
        }

        var builder = new UriBuilder(settings.Endpoint.Trim())
        {
            Query = string.Join('&', query),
        };
        return builder.Uri;
    }

    private async Task SendAudioAsync(
        WebSocket socket,
        ChannelReader<byte[]> audio,
        CancellationToken cancellationToken)
    {
        await foreach (var bytes in audio.ReadAllAsync(cancellationToken))
        {
            await SendJsonAsync(socket, new
            {
                message_type = "input_audio_chunk",
                audio_base_64 = Convert.ToBase64String(bytes),
                commit = false,
                sample_rate = AudioSampleRate,
            }, cancellationToken);
        }
    }

    private async Task ReceiveTranscriptsAsync(
        WebSocket socket,
        string requestedSourceLanguage,
        ChannelWriter<RecognizedSpeechSegment> output,
        FinalCommitTracker finalCommit,
        bool emitPartials,
        CancellationToken cancellationToken)
    {
        var sequence = 0;
        var lastPartialText = string.Empty;
        var lastPartialAt = DateTimeOffset.MinValue;
        while (!cancellationToken.IsCancellationRequested
            && socket.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            var payload = await ReceiveTextMessageAsync(socket, cancellationToken);
            if (payload is null)
            {
                return;
            }
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var messageType = root.TryGetProperty("message_type", out var typeElement)
                ? typeElement.GetString()
                : null;
            if (IsProviderError(messageType))
            {
                throw new RealtimeTranslationUpstreamException(
                    "ElevenLabs Scribe v2 could not continue transcription.");
            }
            if (messageType is not "partial_transcript" and not "committed_transcript_with_timestamps")
            {
                continue;
            }

            var text = root.TryGetProperty("text", out var textElement)
                ? textElement.GetString()?.Trim()
                : null;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var now = _timeProvider.GetUtcNow();
            if (messageType == "partial_transcript")
            {
                if (!emitPartials)
                {
                    continue;
                }
                if (string.Equals(text, lastPartialText, StringComparison.Ordinal)
                    || now - lastPartialAt < PartialEmissionInterval)
                {
                    continue;
                }

                var partialSource = ResolveSourceLanguage(requestedSourceLanguage, null);
                await output.WriteAsync(new RecognizedSpeechSegment(
                    sequence + 1,
                    text,
                    partialSource.Code,
                    partialSource.Locale,
                    now,
                    requestedSourceLanguage == "auto",
                    IsFinal: false), cancellationToken);
                lastPartialText = text;
                lastPartialAt = now;
                continue;
            }

            finalCommit.NotifyCommitted();
            var detectedLanguage = root.TryGetProperty("language_code", out var languageElement)
                ? languageElement.GetString()
                : null;
            var source = ResolveSourceLanguage(requestedSourceLanguage, detectedLanguage);
            await output.WriteAsync(new RecognizedSpeechSegment(
                ++sequence,
                text,
                source.Code,
                source.Locale,
                now,
                requestedSourceLanguage == "auto",
                IsFinal: true), cancellationToken);
            lastPartialText = string.Empty;
            lastPartialAt = DateTimeOffset.MinValue;
        }
    }

    internal RealtimeTranslationSourceLanguageOptions ResolveSourceLanguage(
        string requestedSourceLanguage,
        string? detectedLanguage)
    {
        if (requestedSourceLanguage != "auto")
        {
            return _options.FindSourceLanguage(requestedSourceLanguage)
                ?? FromCatalog(QuizLanguageCatalog.Find(requestedSourceLanguage))
                ?? new RealtimeTranslationSourceLanguageOptions
                {
                    Code = requestedSourceLanguage,
                    Name = requestedSourceLanguage,
                    Locale = requestedSourceLanguage,
                    TranslatorCode = requestedSourceLanguage,
                    ScribeCode = requestedSourceLanguage,
                };
        }

        var normalized = detectedLanguage?.Trim();
        var source = _options.SourceLanguages.FirstOrDefault(language =>
            language.Enabled
            && (string.Equals(language.Code, normalized, StringComparison.OrdinalIgnoreCase)
                || string.Equals(language.Locale, normalized, StringComparison.OrdinalIgnoreCase)
                || string.Equals(language.ScribeCode, normalized, StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    language.Locale.Split('-', 2)[0],
                    normalized,
                    StringComparison.OrdinalIgnoreCase)));
        return source
            ?? FromCatalog(QuizLanguageCatalog.Find(normalized))
            ?? new RealtimeTranslationSourceLanguageOptions
        {
            Code = normalized ?? "auto",
            Name = normalized ?? "Auto detected",
            Locale = normalized ?? "auto",
            TranslatorCode = string.Empty,
            ScribeCode = normalized ?? string.Empty,
        };
    }

    private static RealtimeTranslationSourceLanguageOptions? FromCatalog(QuizLanguage? language) =>
        language is null
            ? null
            : new RealtimeTranslationSourceLanguageOptions
            {
                Code = language.Code,
                Name = language.Name,
                Locale = language.Locale,
                TranslatorCode = language.TranslatorCode,
                ScribeCode = language.ScribeCode,
            };

    private static async Task SendJsonAsync(
        WebSocket socket,
        object message,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        await socket.SendAsync(
            payload,
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    private static async Task<byte[]?> ReceiveTextMessageAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            if (result.MessageType != WebSocketMessageType.Text
                || message.Length + result.Count > MaximumProviderMessageBytes)
            {
                throw new RealtimeTranslationUpstreamException(
                    "ElevenLabs Scribe v2 returned an unsupported response.");
            }
            await message.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
            if (result.EndOfMessage)
            {
                return message.ToArray();
            }
        }
    }

    private static async Task CloseOutputQuietlyAsync(WebSocket socket)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }
        try
        {
            await socket.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure,
                "Transcription finished.",
                CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // The provider already closed the socket.
        }
    }

    private static string Pair(string name, string value) =>
        $"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";

    private static bool IsProviderError(string? messageType) => messageType is
        "error"
        or "auth_error"
        or "quota_exceeded"
        or "transcriber_error"
        or "input_error"
        or "invalid_request"
        or "commit_throttled"
        or "unaccepted_terms"
        or "rate_limited"
        or "queue_overflow"
        or "resource_exhausted"
        or "session_time_limit_exceeded"
        or "chunk_size_exceeded"
        or "insufficient_audio_activity";

    private sealed class FinalCommitTracker
    {
        private readonly TaskCompletionSource _received =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _waiting;

        public void BeginWaiting() => Volatile.Write(ref _waiting, 1);

        public void NotifyCommitted()
        {
            if (Volatile.Read(ref _waiting) == 1)
            {
                _received.TrySetResult();
            }
        }

        public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            _received.Task.WaitAsync(timeout, cancellationToken);
    }
}
