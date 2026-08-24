using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using Microsoft.Extensions.Options;

namespace Glosify.Services.RealtimeTranslation;

public sealed class OpenAiTranslationRelay : IEnhancedTranslationRelay
{
    private static readonly TimeSpan SessionDrainTimeout = TimeSpan.FromSeconds(5);
    private readonly GenerativeAiOptions _openAi;
    private readonly IRealtimeSpeechTranscriber _speech;
    private readonly RealtimeTranslationRelayAuthorizationMonitor _authorizationMonitor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RealtimeTranslationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OpenAiTranslationRelay> _logger;

    public OpenAiTranslationRelay(
        IRealtimeSpeechTranscriber speech,
        RealtimeTranslationRelayAuthorizationMonitor authorizationMonitor,
        IServiceScopeFactory scopeFactory,
        IOptions<RealtimeTranslationOptions> options,
        IOptions<GenerativeAiOptions> openAiOptions,
        TimeProvider timeProvider,
        ILogger<OpenAiTranslationRelay> logger)
    {
        _openAi = openAiOptions.Value;
        _speech = speech;
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
        ArgumentNullException.ThrowIfNull(browserSocket);
        if (!_options.Enabled)
        {
            throw new RealtimeTranslationUnavailableException(
                "Live subtitles are not enabled on this Glosify deployment.");
        }
        if (string.IsNullOrWhiteSpace(_openAi.ApiKey))
        {
            throw new RealtimeTranslationUnavailableException(
                "OpenAI is not configured for live subtitles.");
        }
        var openAiUri = OpenAiTranslationProtocol.BuildWebSocketUri();
        using var openAiSocket = new ClientWebSocket();
        using var browserSendLock = new SemaphoreSlim(1, 1);
        openAiSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        if (authorization.SaveTranscript && !_options.ElevenLabs.Enabled)
        {
            throw new RealtimeTranslationUnavailableException(
                "Saved transcripts require ElevenLabs Scribe v2 on this Glosify deployment.");
        }

        var requestHeaders = OpenAiTranslationProtocol.CreateRequestHeaders(
            _openAi.ApiKey,
            authorization.UserId);
        openAiSocket.Options.SetRequestHeader("Authorization", requestHeaders.Authorization);
        var safetyIdentifier = requestHeaders.SafetyIdentifier;
        openAiSocket.Options.SetRequestHeader(
            "OpenAI-Safety-Identifier",
            safetyIdentifier);
        using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        relayCancellation.CancelAfter(TimeSpan.FromMinutes(_options.MaxSessionMinutes + 1));
        var relayToken = relayCancellation.Token;
        var sessionCloseSent = false;

        try
        {
            await openAiSocket.ConnectAsync(openAiUri, relayToken);
            var sessionUpdate = OpenAiTranslationProtocol.CreateSessionUpdate(
                authorization.TargetLanguage,
                safetyIdentifier);
            await openAiSocket.SendAsync(
                sessionUpdate,
                WebSocketMessageType.Text,
                endOfMessage: true,
                relayToken);

            await WaitForOpenAiSessionUpdatedAsync(openAiSocket, relayToken);
            await SendBrowserControlAsync(browserSocket, "glosify.relay.ready", null, relayToken);
            var sessionState = await _authorizationMonitor.WaitForSessionStartAsync(
                authorization,
                relayToken);
            var billingState = new RealtimeTranslationRelayBillingState(sessionState.ChargedMinutes);
            var transcriptState = new RelayTranscriptState();
            Channel<CapturedTranslationSegment>? transcriptChannel = null;
            Task? transcriptWriter = null;
            Channel<byte[]>? sourceAudio = null;
            Channel<RecognizedSpeechSegment>? sourceSegments = null;
            if (sessionState.TranscriptId.HasValue)
            {
                transcriptChannel = Channel.CreateBounded<CapturedTranslationSegment>(new BoundedChannelOptions(256)
                {
                    SingleReader = true,
                    // The source and translation pumps both write finalized segments.
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait,
                });
                transcriptWriter = WriteTranscriptAsync(
                    authorization.SessionId,
                    transcriptChannel.Reader,
                    transcriptState,
                    CancellationToken.None);
                sourceAudio = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.Wait,
                });
                sourceSegments = Channel.CreateBounded<RecognizedSpeechSegment>(new BoundedChannelOptions(64)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.Wait,
                });
            }

            using var inputCancellation = CancellationTokenSource.CreateLinkedTokenSource(relayToken);
            var browserToOpenAi = PumpBrowserToOpenAiAsync(
                browserSocket,
                openAiSocket,
                sourceAudio?.Writer,
                transcriptState,
                sessionState.StartedAt!.Value,
                billingState,
                inputCancellation.Token);
            var openAiToBrowser = PumpOpenAiToBrowserAsync(
                openAiSocket,
                browserSocket,
                browserSendLock,
                authorization.SessionId,
                transcriptChannel?.Writer,
                transcriptState,
                relayToken);
            var sourceTranscription = sourceAudio is null || sourceSegments is null
                ? Task.Delay(Timeout.InfiniteTimeSpan, relayToken)
                : _speech.TranscribeAsync(
                    RealtimeSpeechProviders.ElevenLabs,
                    authorization.SourceLanguage ?? "auto",
                    sourceAudio.Reader,
                    sourceSegments.Writer,
                    emitPartials: false,
                    cancellationToken: relayToken);
            var sourceToTranscript = sourceSegments is null
                ? Task.Delay(Timeout.InfiniteTimeSpan, relayToken)
                : PumpScribeTranscriptAsync(
                    sourceSegments.Reader,
                    browserSocket,
                    browserSendLock,
                    authorization.SessionId,
                    transcriptChannel?.Writer,
                    transcriptState,
                    relayToken);
            var authorizationMonitor = _authorizationMonitor.MonitorAuthorizationAsync(
                authorization,
                sessionState.StartedAt.Value,
                billingState,
                relayToken);

            var completed = await Task.WhenAny(
                browserToOpenAi,
                openAiToBrowser,
                sourceTranscription,
                sourceToTranscript,
                authorizationMonitor);
            try
            {
                Exception? completionError = null;
                try
                {
                    if (completed == sourceTranscription || completed == sourceToTranscript)
                    {
                        await AwaitScribePipelineAsync(sourceTranscription, sourceToTranscript);
                    }
                    else
                    {
                        await completed;
                    }
                }
                catch (Exception exception)
                {
                    completionError = exception;
                }

                if (completed != openAiToBrowser && !relayToken.IsCancellationRequested)
                {
                    // Stop accepting audio before asking OpenAI to close, then keep the
                    // receive pump alive until it has forwarded every final caption and
                    // observed session.closed (or the bounded drain expires).
                    inputCancellation.Cancel();
                    await IgnoreCancellationAsync(browserToOpenAi);
                    if (sourceAudio is not null)
                    {
                        try
                        {
                            await AwaitScribePipelineAsync(sourceTranscription, sourceToTranscript);
                        }
                        catch (Exception exception)
                        {
                            completionError ??= exception;
                        }
                    }
                    sessionCloseSent = await DrainOpenAiSessionAsync(
                        openAiSocket,
                        openAiToBrowser,
                        SessionDrainTimeout,
                        CancellationToken.None);
                    if (!openAiToBrowser.IsCompleted)
                    {
                        _logger.LogWarning(
                            "Timed out waiting for OpenAI to finish subtitle session {SessionId}",
                            authorization.SessionId);
                    }
                }

                if (completionError is not null)
                {
                    ExceptionDispatchInfo.Capture(completionError).Throw();
                }
            }
            finally
            {
                relayCancellation.Cancel();
                await IgnoreCancellationAsync(Task.WhenAll(
                    browserToOpenAi,
                    openAiToBrowser,
                    sourceTranscription,
                    sourceToTranscript,
                    authorizationMonitor));
                transcriptChannel?.Writer.TryComplete();
                if (transcriptWriter is not null)
                {
                    try
                    {
                        // CancellationToken.None on purpose. This runs in the teardown path,
                        // after relayCancellation.Cancel(), so the caller's token is normally
                        // already cancelled — forwarding it would abandon the caption flush
                        // instead of giving it the five seconds this drain exists to provide,
                        // and the OperationCanceledException would escape the TimeoutException
                        // catch below and mask whatever was already unwinding.
                        await transcriptWriter.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                    }
                    catch (TimeoutException)
                    {
                        _logger.LogWarning(
                            "Timed out while flushing saved captions for session {SessionId}",
                            authorization.SessionId);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (relayToken.IsCancellationRequested)
        {
            // Normal request abort, shutdown, session timeout, or peer closure.
        }
        catch (WebSocketException exception)
        {
            RealtimeTranslationTelemetry.UpstreamFailures.Add(1);
            _logger.LogWarning(
                exception,
                "OpenAI subtitle relay transport failed for session {SessionId}",
                authorization.SessionId);
            throw new RealtimeTranslationUpstreamException(
                "OpenAI ended the live subtitle connection.");
        }
        finally
        {
            if (!sessionCloseSent)
            {
                await SendSessionCloseQuietlyAsync(openAiSocket);
            }
            await CloseQuietlyAsync(openAiSocket, CancellationToken.None);
            await CloseQuietlyAsync(browserSocket, CancellationToken.None);
        }
    }

    private async Task WaitForOpenAiSessionUpdatedAsync(
        WebSocket openAiSocket,
        CancellationToken cancellationToken)
    {
        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupCancellation.CancelAfter(TimeSpan.FromSeconds(_options.RelayStartupTimeoutSeconds));
        try
        {
            while (true)
            {
                var message = await ReceiveTextMessageAsync(
                    openAiSocket,
                    OpenAiTranslationProtocol.MaximumOpenAiMessageBytes,
                    startupCancellation.Token);
                if (message is null)
                {
                    throw new RealtimeTranslationUpstreamException(
                        "OpenAI ended the subtitle connection during setup.");
                }
                if (OpenAiTranslationProtocol.HasType(message, "session.updated"))
                {
                    return;
                }
                if (OpenAiTranslationProtocol.HasType(message, "error"))
                {
                    throw new RealtimeTranslationUpstreamException(
                        "OpenAI rejected the live subtitle configuration.");
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RealtimeTranslationUpstreamException(
                "OpenAI timed out while configuring live subtitles.");
        }
    }

    private async Task PumpBrowserToOpenAiAsync(
        WebSocket browserSocket,
        WebSocket openAiSocket,
        ChannelWriter<byte[]>? sourceAudio,
        RelayTranscriptState transcriptState,
        DateTimeOffset startedAt,
        RealtimeTranslationRelayBillingState billingState,
        CancellationToken cancellationToken)
    {
        long forwardedAudioBytes = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested
                && browserSocket.State == WebSocketState.Open
                && openAiSocket.State == WebSocketState.Open)
            {
                var message = await ReceiveTextMessageAsync(
                    browserSocket,
                    OpenAiTranslationProtocol.MaximumBrowserMessageBytes,
                    cancellationToken);
                if (message is null)
                {
                    return;
                }
                if (OpenAiTranslationProtocol.IsBrowserCloseRequest(message))
                {
                    return;
                }
                if (!OpenAiTranslationProtocol.TryDecodeBrowserAudio(message, out var audioBytes))
                {
                    await browserSocket.CloseOutputAsync(
                        WebSocketCloseStatus.PolicyViolation,
                        "Only bounded subtitle audio messages are accepted.",
                        cancellationToken);
                    return;
                }

                await _authorizationMonitor.WaitForAudioCapacityAsync(
                    forwardedAudioBytes + audioBytes.Length,
                    startedAt,
                    billingState,
                    cancellationToken);

                await openAiSocket.SendAsync(
                    message,
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken);
                if (sourceAudio is not null
                    && !sourceAudio.TryWrite(audioBytes)
                    && Interlocked.Exchange(ref transcriptState.WarningPending, 1) == 0)
                {
                    _logger.LogWarning(
                        "Scribe source-audio buffer filled; live subtitles will continue with a transcript gap.");
                }
                forwardedAudioBytes += audioBytes.Length;
            }
        }
        finally
        {
            sourceAudio?.TryComplete();
        }
    }

    private async Task PumpOpenAiToBrowserAsync(
        WebSocket openAiSocket,
        WebSocket browserSocket,
        SemaphoreSlim browserSendLock,
        Guid sessionId,
        ChannelWriter<CapturedTranslationSegment>? transcriptWriter,
        RelayTranscriptState transcriptState,
        CancellationToken cancellationToken)
    {
        // The translated captions are already on this socket for the live overlay, so
        // saving them alongside the source costs no extra OpenAI usage.
        var accumulator = transcriptWriter is null
            ? null
            : new OpenAiTranslationTranscriptAccumulator();
        try
        {
            while (!cancellationToken.IsCancellationRequested
                && openAiSocket.State == WebSocketState.Open)
            {
                var message = await ReceiveTextMessageAsync(
                    openAiSocket,
                    OpenAiTranslationProtocol.MaximumOpenAiMessageBytes,
                    cancellationToken);
                if (message is null)
                {
                    return;
                }
                if (accumulator is not null)
                {
                    var now = _timeProvider.GetUtcNow();
                    var segment = accumulator.Apply(message, now);
                    if (segment is not null)
                    {
                        WriteCaption(segment, transcriptWriter!, transcriptState, sessionId);
                    }
                    foreach (var idle in accumulator.FlushIdle(now))
                    {
                        WriteCaption(idle, transcriptWriter!, transcriptState, sessionId);
                    }
                }
                if (OpenAiTranslationProtocol.HasType(message, "session.closed"))
                {
                    if (browserSocket.State == WebSocketState.Open)
                    {
                        await browserSendLock.WaitAsync(cancellationToken);
                        try
                        {
                            await SendBrowserControlAsync(
                                browserSocket,
                                "glosify.relay.closed",
                                null,
                                cancellationToken);
                        }
                        finally
                        {
                            browserSendLock.Release();
                        }
                    }
                    return;
                }
                if (!OpenAiTranslationProtocol.ShouldForwardOpenAiMessage(message))
                {
                    continue;
                }

                if (browserSocket.State == WebSocketState.Open)
                {
                    await SendBrowserBytesAsync(browserSocket, browserSendLock, message, cancellationToken);
                }
            }
        }
        finally
        {
            if (accumulator is not null)
            {
                foreach (var remaining in accumulator.FlushAll(_timeProvider.GetUtcNow()))
                {
                    WriteCaption(remaining, transcriptWriter!, transcriptState, sessionId);
                }
                // Records which caption events the translate deployment actually
                // sends, so segmentation can be corrected without guesswork. Event
                // types and id-field names only; never caption text.
                _logger.LogInformation(
                    "Translation capture for session {SessionId} saw event types {EventTypes}",
                    sessionId,
                    string.Join(", ", accumulator.ObservedEventTypes.OrderBy(type => type)));
            }
        }
    }

    private void WriteCaption(
        CapturedTranslationSegment segment,
        ChannelWriter<CapturedTranslationSegment> writer,
        RelayTranscriptState transcriptState,
        Guid sessionId)
    {
        if (writer.TryWrite(segment))
        {
            return;
        }
        Interlocked.Exchange(ref transcriptState.WarningPending, 1);
        _logger.LogWarning(
            "Saved caption buffer filled for session {SessionId}",
            sessionId);
    }

    private async Task PumpScribeTranscriptAsync(
        ChannelReader<RecognizedSpeechSegment> sourceSegments,
        WebSocket browserSocket,
        SemaphoreSlim browserSendLock,
        Guid sessionId,
        ChannelWriter<CapturedTranslationSegment>? transcriptWriter,
        RelayTranscriptState transcriptState,
        CancellationToken cancellationToken)
    {
        await foreach (var recognized in sourceSegments.ReadAllAsync(cancellationToken))
        {
            if (transcriptWriter is not null)
            {
                WriteCaption(new CapturedTranslationSegment(
                    recognized.Sequence,
                    $"scribe:source:{recognized.Sequence}",
                    recognized.Text,
                    recognized.CapturedAt), transcriptWriter, transcriptState, sessionId);
            }

            if (browserSocket.State != WebSocketState.Open)
            {
                continue;
            }
            if (Interlocked.Exchange(ref transcriptState.WarningPending, 0) == 1)
            {
                await browserSendLock.WaitAsync(cancellationToken);
                try
                {
                    await SendBrowserControlAsync(
                        browserSocket,
                        "glosify.transcript.warning",
                        "Live subtitles are continuing, but part of the saved transcript could not be stored.",
                        cancellationToken);
                }
                finally
                {
                    browserSendLock.Release();
                }
            }
        }
    }

    private static async Task SendBrowserBytesAsync(
        WebSocket browserSocket,
        SemaphoreSlim sendLock,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        await sendLock.WaitAsync(cancellationToken);
        try
        {
            await browserSocket.SendAsync(
                payload,
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private async Task WriteTranscriptAsync(
        Guid sessionId,
        ChannelReader<CapturedTranslationSegment> reader,
        RelayTranscriptState state,
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
                var transcripts = scope.ServiceProvider.GetRequiredService<IRealtimeTranslationTranscriptService>();
                await transcripts.AppendAsync(sessionId, batch, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Interlocked.Exchange(ref state.WarningPending, 1);
                _logger.LogWarning(
                    exception,
                    "Could not store finalized captions for session {SessionId}",
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
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Min(16 * 1024, maximumBytes)];
        using var message = new MemoryStream(buffer.Length);
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            if (result.MessageType != WebSocketMessageType.Text
                || message.Length + result.Count > maximumBytes)
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

    internal static async Task SendBrowserControlAsync(
        WebSocket browserSocket,
        string type,
        string? message,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { type, message });
        await browserSocket.SendAsync(
            payload,
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    private static async Task CloseQuietlyAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
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
                cancellationToken);
        }
        catch (WebSocketException)
        {
            // The other peer already closed the transport.
        }
    }

    private static async Task SendSessionCloseQuietlyAsync(WebSocket socket)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }
        try
        {
            await socket.SendAsync(
                OpenAiTranslationProtocol.CreateSessionClose(),
                WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // The upstream already closed the translation session.
        }
    }

    internal static async Task<bool> DrainOpenAiSessionAsync(
        WebSocket openAiSocket,
        Task receivePump,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (openAiSocket.State != WebSocketState.Open)
        {
            return false;
        }

        using var drainCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        drainCancellation.CancelAfter(timeout);
        var closeSent = false;
        try
        {
            await openAiSocket.SendAsync(
                OpenAiTranslationProtocol.CreateSessionClose(),
                WebSocketMessageType.Text,
                endOfMessage: true,
                drainCancellation.Token);
            closeSent = true;
            await receivePump.WaitAsync(drainCancellation.Token);
            return true;
        }
        catch (OperationCanceledException) when (drainCancellation.IsCancellationRequested)
        {
            // Do not send a duplicate from the outer teardown merely because OpenAI
            // missed the drain deadline after accepting the close request.
            return closeSent;
        }
        catch (WebSocketException)
        {
            return false;
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
        catch (RealtimeTranslationUpstreamException)
        {
            // The task selected by WhenAny already propagates the provider failure.
        }
    }

    internal static async Task AwaitScribePipelineAsync(
        Task sourceTranscription,
        Task sourceToTranscript)
    {
        await sourceToTranscript;
        await sourceTranscription;
    }

    private sealed class RelayTranscriptState
    {
        public int WarningPending;
    }
}
