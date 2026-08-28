using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Glosify.Models.Entities;
using Microsoft.Extensions.Options;

namespace Glosify.Services.RealtimeTranslation;

public interface IScribeTranslationRelay
{
    Task RelayAsync(
        WebSocket browserSocket,
        RealtimeTranslationRelayAuthorization authorization,
        CancellationToken cancellationToken = default);
}

public sealed class ScribeTranslationRelay : IScribeTranslationRelay
{
    private readonly IRealtimeSpeechTranscriber _speech;
    private readonly IEconomicalSubtitleTranslator _translator;
    private readonly ICloudflareSubtitleTranslator _cloudflareTranslator;
    private readonly RealtimeTranslationRelayAuthorizationMonitor _authorizationMonitor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RealtimeTranslationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ScribeTranslationRelay> _logger;

    public ScribeTranslationRelay(
        IRealtimeSpeechTranscriber speech,
        IEconomicalSubtitleTranslator translator,
        ICloudflareSubtitleTranslator cloudflareTranslator,
        RealtimeTranslationRelayAuthorizationMonitor authorizationMonitor,
        IServiceScopeFactory scopeFactory,
        IOptions<RealtimeTranslationOptions> options,
        TimeProvider timeProvider,
        ILogger<ScribeTranslationRelay> logger)
    {
        _speech = speech;
        _translator = translator;
        _cloudflareTranslator = cloudflareTranslator;
        _authorizationMonitor = authorizationMonitor;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task RelayAsync(
        WebSocket browserSocket,
        RealtimeTranslationRelayAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        var supportedMode = authorization.TranslationMode switch
        {
            RealtimeTranslationModes.Scribe => _options.ElevenLabs.Enabled,
            RealtimeTranslationModes.ScribeCloudflare =>
                _options.ElevenLabs.Enabled && _options.Cloudflare.Enabled,
            _ => false,
        };
        if (!supportedMode
            || string.IsNullOrWhiteSpace(authorization.SourceLanguage))
        {
            throw new RealtimeTranslationUnavailableException(
                "The selected speech recognition mode is not configured on this Glosify deployment.");
        }

        var captureAdminSession = await IsAdminCaptureEnabledAsync(
            authorization.UserId,
            cancellationToken);

        using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        relayCancellation.CancelAfter(TimeSpan.FromMinutes(_options.MaxSessionMinutes + 1));
        var relayToken = relayCancellation.Token;
        var audio = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var recognized = Channel.CreateBounded<RecognizedSpeechSegment>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        Channel<CapturedTranslationSegment>? transcripts = authorization.SaveTranscript
            ? Channel.CreateBounded<CapturedTranslationSegment>(new BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            })
            : null;
        Channel<CapturedRealtimeTranslationEvent>? captures = captureAdminSession
            ? Channel.CreateBounded<CapturedRealtimeTranslationEvent>(new BoundedChannelOptions(1024)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            })
            : null;
        Task? transcriptWriter = null;
        Task? captureWriter = null;
        var captureRecorder = captures is null
            ? null
            : new AdminCaptureRecorder(captures.Writer, _timeProvider);

        try
        {
            await OpenAiTranslationRelay.SendBrowserControlAsync(
                browserSocket,
                "glosify.relay.ready",
                null,
                relayToken);
            var session = await _authorizationMonitor.WaitForSessionStartAsync(
                authorization,
                relayToken);
            var billing = new RealtimeTranslationRelayBillingState(session.ChargedMinutes);
            if (transcripts is not null)
            {
                transcriptWriter = WriteTranscriptsAsync(
                    authorization.SessionId,
                    transcripts.Reader,
                    CancellationToken.None);
            }
            if (captures is not null)
            {
                captureWriter = WriteCapturesAsync(
                    authorization.SessionId,
                    authorization.UserId,
                    captures.Reader,
                    CancellationToken.None);
            }

            var browserPump = PumpBrowserAudioAsync(
                browserSocket,
                audio.Writer,
                session.StartedAt!.Value,
                billing,
                relayToken);
            var speechPump = _speech.TranscribeAsync(
                authorization.SpeechProvider,
                authorization.SourceLanguage,
                audio.Reader,
                recognized.Writer,
                emitPartials: authorization.PartialCaptionsEnabled
                    && (_options.ElevenLabs.TranslatePartials || captureAdminSession),
                cancellationToken: relayToken);
            var translationPump = TranslateAndSendAsync(
                browserSocket,
                authorization,
                recognized.Reader,
                transcripts?.Writer,
                captureRecorder,
                relayToken);
            var authorizationMonitor = _authorizationMonitor.MonitorAuthorizationAsync(
                authorization,
                session.StartedAt.Value,
                billing,
                relayToken);

            var completed = await Task.WhenAny(
                browserPump,
                speechPump,
                translationPump,
                authorizationMonitor);
            try
            {
                if (completed == browserPump)
                {
                    await browserPump;
                    audio.Writer.TryComplete();
                    await speechPump;
                    await translationPump;
                    if (browserSocket.State == WebSocketState.Open)
                    {
                        await OpenAiTranslationRelay.SendBrowserControlAsync(
                            browserSocket,
                            "glosify.relay.closed",
                            null,
                            relayToken);
                    }
                }
                else
                {
                    await completed;
                    if (!relayToken.IsCancellationRequested)
                    {
                        throw new RealtimeTranslationUpstreamException(
                            "The selected subtitle mode ended unexpectedly.");
                    }
                }
            }
            finally
            {
                audio.Writer.TryComplete();
                relayCancellation.Cancel();
                await IgnoreCancellationAsync(Task.WhenAll(
                    browserPump,
                    speechPump,
                    translationPump,
                    authorizationMonitor));
            }
        }
        catch (OperationCanceledException) when (relayToken.IsCancellationRequested)
        {
            // Normal request abort, shutdown, timeout, or peer closure.
        }
        catch (RealtimeTranslationExpiredException)
        {
            throw;
        }
        catch (RealtimeTranslationUnavailableException)
        {
            throw;
        }
        catch (RealtimeTranslationValidationException)
        {
            throw;
        }
        catch (RealtimeTranslationUpstreamException)
        {
            RealtimeTranslationTelemetry.UpstreamFailures.Add(1);
            throw;
        }
        catch (WebSocketException exception)
        {
            RealtimeTranslationTelemetry.UpstreamFailures.Add(1);
            _logger.LogWarning(
                exception,
                "Speech-recognition subtitle transport failed for session {SessionId}",
                authorization.SessionId);
            throw new RealtimeTranslationUpstreamException(
                "The selected subtitle connection ended.");
        }
        catch (Exception exception)
        {
            RealtimeTranslationTelemetry.UpstreamFailures.Add(1);
            _logger.LogWarning(
                exception,
                "Speech-recognition subtitle provider failed for session {SessionId}",
                authorization.SessionId);
            throw new RealtimeTranslationUpstreamException(
                "The selected subtitle provider ended the session.");
        }
        finally
        {
            relayCancellation.Cancel();
            audio.Writer.TryComplete();
            recognized.Writer.TryComplete();
            transcripts?.Writer.TryComplete();
            captures?.Writer.TryComplete();
            var storageWriters = new[] { transcriptWriter, captureWriter }
                .Where(task => task is not null)
                .Cast<Task>()
                .ToArray();
            if (storageWriters.Length > 0)
            {
                try
                {
                    await Task.WhenAll(storageWriters)
                        .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning(
                        "Timed out while flushing speech-recognition data for session {SessionId}",
                        authorization.SessionId);
                }
            }
            await CloseQuietlyAsync(browserSocket);
        }
    }

    private async Task PumpBrowserAudioAsync(
        WebSocket browserSocket,
        ChannelWriter<byte[]> audio,
        DateTimeOffset startedAt,
        RealtimeTranslationRelayBillingState billing,
        CancellationToken cancellationToken)
    {
        long forwardedBytes = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested
                && browserSocket.State == WebSocketState.Open)
            {
                var message = await ReceiveTextMessageAsync(browserSocket, cancellationToken);
                if (message is null)
                {
                    return;
                }
                if (OpenAiTranslationProtocol.IsBrowserCloseRequest(message))
                {
                    return;
                }
                if (!OpenAiTranslationProtocol.TryDecodeBrowserAudio(message, out var bytes))
                {
                    await browserSocket.CloseOutputAsync(
                        WebSocketCloseStatus.PolicyViolation,
                        "Only bounded subtitle audio messages are accepted.",
                        cancellationToken);
                    return;
                }
                await _authorizationMonitor.WaitForAudioCapacityAsync(
                    forwardedBytes + bytes.Length,
                    startedAt,
                    billing,
                    cancellationToken);
                await audio.WriteAsync(bytes, cancellationToken);
                forwardedBytes += bytes.Length;
            }
        }
        finally
        {
            audio.TryComplete();
        }
    }

    private async Task TranslateAndSendAsync(
        WebSocket browserSocket,
        RealtimeTranslationRelayAuthorization authorization,
        ChannelReader<RecognizedSpeechSegment> recognized,
        ChannelWriter<CapturedTranslationSegment>? transcripts,
        AdminCaptureRecorder? captureRecorder,
        CancellationToken cancellationToken)
    {
        var scheduler = new AdaptivePartialTranslationScheduler(
            _timeProvider,
            _options.ElevenLabs,
            translatePartials: authorization.PartialCaptionsEnabled
                && (_options.ElevenLabs.TranslatePartials || captureRecorder is not null),
            partialInterval: authorization.TranslationMode == RealtimeTranslationModes.ScribeCloudflare
                ? TimeSpan.FromSeconds(_options.Cloudflare.PartialIntervalSeconds)
                : null,
            paceFromRequestStart:
                authorization.TranslationMode == RealtimeTranslationModes.ScribeCloudflare,
            ignorePartialTranslationFailures:
                authorization.TranslationMode == RealtimeTranslationModes.ScribeCloudflare);
        var bubbleFinalizer = new TranslationBubbleFinalizer();
        await scheduler.RunAsync(
            recognized,
            (segment, token) => TranslateAsync(
                authorization.TranslationMode,
                segment,
                authorization.TargetLanguage,
                token),
            async (segment, result, providerRequest, token) =>
            {
                var bubbleUpdate = bubbleFinalizer.Apply(
                    segment.Sequence,
                    result.TranslatedText,
                    segment.IsFinal);
                if (captureRecorder is not null)
                {
                    await captureRecorder.RecordTranslationAsync(
                        segment,
                        result,
                        bubbleUpdate,
                        providerRequest,
                        token);
                }
                await SendTranslationAsync(
                    browserSocket,
                    segment,
                    result,
                    bubbleUpdate,
                    transcripts,
                    token);
            },
            cancellationToken,
            captureRecorder is null ? null : captureRecorder.RecordSourceAsync);
    }

    private Task<TranslatedSubtitleSegment> TranslateAsync(
        string translationMode,
        RecognizedSpeechSegment segment,
        string targetLanguage,
        CancellationToken cancellationToken) => translationMode switch
        {
            RealtimeTranslationModes.Scribe => _translator.TranslateAsync(
                segment,
                targetLanguage,
                cancellationToken),
            RealtimeTranslationModes.ScribeCloudflare => _cloudflareTranslator.TranslateAsync(
                segment,
                targetLanguage,
                cancellationToken),
            _ => throw new RealtimeTranslationValidationException(
                "The requested Scribe translation mode is not supported."),
        };

    private static async Task SendTranslationAsync(
        WebSocket browserSocket,
        RecognizedSpeechSegment segment,
        TranslatedSubtitleSegment result,
        TranslationBubbleUpdate bubbleUpdate,
        ChannelWriter<CapturedTranslationSegment>? transcripts,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = segment.IsFinal
                ? "glosify.translation.segment"
                : "glosify.translation.partial",
            sequence = segment.Sequence,
            sourceLanguage = result.SourceLanguage,
            targetLanguage = result.TargetLanguage,
            text = result.TranslatedText,
            committedBubbles = bubbleUpdate.CommittedBubbles,
            pendingText = bubbleUpdate.PendingText,
        });
        await browserSocket.SendAsync(
            payload,
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);

        if (transcripts is not null && segment.IsFinal)
        {
            await transcripts.WriteAsync(new CapturedTranslationSegment(
                segment.Sequence,
                $"scribe:source:{segment.Sequence}",
                result.SourceText,
                result.CapturedAt), cancellationToken);
            await transcripts.WriteAsync(new CapturedTranslationSegment(
                segment.Sequence,
                $"scribe:translation:{segment.Sequence}",
                result.TranslatedText,
                result.CapturedAt,
                RealtimeTranslationTranscriptStreams.Translation), cancellationToken);
        }
    }

    private async Task<bool> IsAdminCaptureEnabledAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IRealtimeTranslationCaptureService>();
            return await service.IsAdminUserAsync(userId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Could not determine whether Scribe diagnostics are enabled for the current account");
            return false;
        }
    }

    private async Task WriteCapturesAsync(
        Guid sessionId,
        string userId,
        ChannelReader<CapturedRealtimeTranslationEvent> reader,
        CancellationToken cancellationToken)
    {
        var batch = new List<CapturedRealtimeTranslationEvent>(50);
        await foreach (var captured in reader.ReadAllAsync(cancellationToken))
        {
            batch.Add(captured);
            while (batch.Count < 50 && reader.TryRead(out var next))
            {
                batch.Add(next);
            }
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IRealtimeTranslationCaptureService>();
                await service.AppendAsync(sessionId, userId, batch, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(
                    exception,
                    "Could not store admin Scribe diagnostics for session {SessionId}",
                    sessionId);
            }
            finally
            {
                batch.Clear();
            }
        }
    }

    private async Task WriteTranscriptsAsync(
        Guid sessionId,
        ChannelReader<CapturedTranslationSegment> reader,
        CancellationToken cancellationToken)
    {
        var batch = new List<CapturedTranslationSegment>(20);
        await foreach (var segment in reader.ReadAllAsync(cancellationToken))
        {
            batch.Add(segment);
            while (batch.Count < 20 && reader.TryRead(out var next))
            {
                batch.Add(next);
            }
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IRealtimeTranslationTranscriptService>();
                await service.AppendAsync(sessionId, batch, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(
                    exception,
                    "Could not store Scribe captions for session {SessionId}",
                    sessionId);
            }
            finally
            {
                batch.Clear();
            }
        }
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
                || message.Length + result.Count > OpenAiTranslationProtocol.MaximumBrowserMessageBytes)
            {
                throw new WebSocketException("The relay received an unsupported WebSocket message.");
            }
            await message.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
            if (result.EndOfMessage)
            {
                return message.ToArray();
            }
        }
    }

    private static async Task CloseQuietlyAsync(WebSocket socket)
    {
        if (socket.State is not WebSocketState.Open and not WebSocketState.CloseReceived)
        {
            return;
        }
        try
        {
            await socket.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure,
                "Subtitle relay ended.",
                CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // The other peer already closed the transport.
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Expected when another relay task completes first.
        }
        catch (WebSocketException)
        {
            // Expected when cancellation races a peer closure.
        }
    }

    private sealed class AdminCaptureRecorder(
        ChannelWriter<CapturedRealtimeTranslationEvent> writer,
        TimeProvider timeProvider)
    {
        private int _ordinal;

        public Task RecordSourceAsync(
            RecognizedSpeechSegment segment,
            CancellationToken cancellationToken) =>
            WriteAsync(
                segment.Sequence,
                RealtimeTranslationCaptureStages.Scribe,
                segment.IsFinal
                    ? RealtimeTranslationCaptureKinds.Final
                    : RealtimeTranslationCaptureKinds.Partial,
                segment.Text,
                null,
                segment.SourceLanguage,
                null,
                providerRequest: false,
                segment.CapturedAt,
                cancellationToken);

        public async Task RecordTranslationAsync(
            RecognizedSpeechSegment segment,
            TranslatedSubtitleSegment result,
            TranslationBubbleUpdate bubbleUpdate,
            bool providerRequest,
            CancellationToken cancellationToken)
        {
            var capturedAt = timeProvider.GetUtcNow();
            await WriteAsync(
                segment.Sequence,
                RealtimeTranslationCaptureStages.Translator,
                segment.IsFinal
                    ? RealtimeTranslationCaptureKinds.Final
                    : RealtimeTranslationCaptureKinds.Partial,
                result.TranslatedText,
                result.SourceText,
                result.SourceLanguage,
                result.TargetLanguage,
                providerRequest,
                capturedAt,
                cancellationToken);
            foreach (var bubble in bubbleUpdate.CommittedBubbles)
            {
                await WriteAsync(
                    segment.Sequence,
                    RealtimeTranslationCaptureStages.Bubble,
                    RealtimeTranslationCaptureKinds.Final,
                    bubble,
                    result.SourceText,
                    result.SourceLanguage,
                    result.TargetLanguage,
                    providerRequest: false,
                    capturedAt,
                    cancellationToken);
            }
        }

        private Task WriteAsync(
            int sequence,
            string stage,
            string kind,
            string text,
            string? sourceText,
            string? sourceLanguage,
            string? targetLanguage,
            bool providerRequest,
            DateTimeOffset capturedAt,
            CancellationToken cancellationToken) =>
            writer.WriteAsync(new CapturedRealtimeTranslationEvent(
                Interlocked.Increment(ref _ordinal),
                sequence,
                stage,
                kind,
                text,
                sourceText,
                sourceLanguage,
                targetLanguage,
                providerRequest,
                capturedAt), cancellationToken).AsTask();
    }
}
