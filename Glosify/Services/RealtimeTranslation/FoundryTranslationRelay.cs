using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Azure.Core;
using Azure.Identity;
using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glosify.Services.RealtimeTranslation;

public sealed class FoundryTranslationRelay : IFoundryTranslationRelay
{
    private const string FoundryTokenScope = "https://ai.azure.com/.default";
    private const int PcmBytesPerSecond = 24_000 * sizeof(short);

    private readonly TokenCredential _credential;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RealtimeTranslationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FoundryTranslationRelay> _logger;

    public FoundryTranslationRelay(
        TokenCredential credential,
        IServiceScopeFactory scopeFactory,
        IOptions<RealtimeTranslationOptions> options,
        TimeProvider timeProvider,
        ILogger<FoundryTranslationRelay> logger)
    {
        _credential = credential;
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

        var foundryUri = FoundryTranslationProtocol.BuildWebSocketUri(_options);
        using var foundrySocket = new ClientWebSocket();
        using var sourceSocket = authorization.SaveTranscript ? new ClientWebSocket() : null;
        using var browserSendLock = new SemaphoreSlim(1, 1);
        foundrySocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        if (sourceSocket is not null)
        {
            sourceSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        }

        AccessToken token;
        try
        {
            token = await _credential.GetTokenAsync(
                new TokenRequestContext([FoundryTokenScope]),
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            RealtimeTranslationTelemetry.UpstreamFailures.Add(1);
            throw new RealtimeTranslationUpstreamException(
                "Microsoft Foundry timed out while authorizing live subtitles.");
        }
        catch (AuthenticationFailedException)
        {
            RealtimeTranslationTelemetry.UpstreamFailures.Add(1);
            throw new RealtimeTranslationUpstreamException(
                "Microsoft Foundry could not authorize live subtitles.");
        }

        foundrySocket.Options.SetRequestHeader(
            "Authorization",
            new AuthenticationHeaderValue("Bearer", token.Token).ToString());
        sourceSocket?.Options.SetRequestHeader(
            "Authorization",
            new AuthenticationHeaderValue("Bearer", token.Token).ToString());

        using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        relayCancellation.CancelAfter(TimeSpan.FromMinutes(_options.MaxSessionMinutes + 1));
        var relayToken = relayCancellation.Token;

        try
        {
            await foundrySocket.ConnectAsync(foundryUri, relayToken);
            var sessionUpdate = FoundryTranslationProtocol.CreateSessionUpdate(
                authorization.TargetLanguage);
            await foundrySocket.SendAsync(
                sessionUpdate,
                WebSocketMessageType.Text,
                endOfMessage: true,
                relayToken);

            await WaitForFoundrySessionUpdatedAsync(foundrySocket, relayToken);
            if (sourceSocket is not null)
            {
                var sourceLanguage = authorization.SourceLanguage
                    ?? throw new InvalidOperationException(
                        "Saved source transcription is missing its quiz language.");
                await sourceSocket.ConnectAsync(
                    FoundryTranslationProtocol.BuildSourceTranscriptionWebSocketUri(_options),
                    relayToken);
                await sourceSocket.SendAsync(
                    FoundryTranslationProtocol.CreateSourceTranscriptionSessionUpdate(
                        _options,
                        sourceLanguage),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    relayToken);
                await WaitForFoundrySessionUpdatedAsync(sourceSocket, relayToken);
            }
            await SendBrowserControlAsync(browserSocket, "glosify.relay.ready", null, relayToken);
            var sessionState = await WaitForSessionStartAsync(authorization, relayToken);
            var billingState = new RelayBillingState(sessionState.ChargedMinutes);
            var transcriptState = new RelayTranscriptState();
            Channel<CapturedTranslationSegment>? transcriptChannel = null;
            Task? transcriptWriter = null;
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
            }

            var browserToFoundry = PumpBrowserToFoundryAsync(
                browserSocket,
                foundrySocket,
                sourceSocket,
                sessionState.StartedAt!.Value,
                billingState,
                relayToken);
            var foundryToBrowser = PumpFoundryToBrowserAsync(
                foundrySocket,
                browserSocket,
                browserSendLock,
                authorization.SessionId,
                transcriptChannel?.Writer,
                transcriptState,
                relayToken);
            var sourceToBrowser = sourceSocket is null
                ? Task.Delay(Timeout.InfiniteTimeSpan, relayToken)
                : PumpSourceTranscriptAsync(
                    sourceSocket,
                    browserSocket,
                    browserSendLock,
                    authorization.SessionId,
                    transcriptChannel?.Writer,
                    transcriptState,
                    relayToken);
            var authorizationMonitor = MonitorAuthorizationAsync(
                authorization,
                sessionState.StartedAt.Value,
                billingState,
                relayToken);

            var completed = await Task.WhenAny(
                browserToFoundry,
                foundryToBrowser,
                sourceToBrowser,
                authorizationMonitor);
            relayCancellation.Cancel();

            try
            {
                await completed;
            }
            finally
            {
                await IgnoreCancellationAsync(Task.WhenAll(
                    browserToFoundry,
                    foundryToBrowser,
                    sourceToBrowser,
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
                "Foundry subtitle relay transport failed for session {SessionId}",
                authorization.SessionId);
            throw new RealtimeTranslationUpstreamException(
                "Microsoft Foundry ended the live subtitle connection.");
        }
        finally
        {
            await CloseQuietlyAsync(foundrySocket, CancellationToken.None);
            if (sourceSocket is not null)
            {
                await CloseQuietlyAsync(sourceSocket, CancellationToken.None);
            }
            await CloseQuietlyAsync(browserSocket, CancellationToken.None);
        }
    }

    private async Task WaitForFoundrySessionUpdatedAsync(
        WebSocket foundrySocket,
        CancellationToken cancellationToken)
    {
        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupCancellation.CancelAfter(TimeSpan.FromSeconds(_options.RelayStartupTimeoutSeconds));
        try
        {
            while (true)
            {
                var message = await ReceiveTextMessageAsync(
                    foundrySocket,
                    FoundryTranslationProtocol.MaximumFoundryMessageBytes,
                    startupCancellation.Token);
                if (message is null)
                {
                    throw new RealtimeTranslationUpstreamException(
                        "Microsoft Foundry ended the subtitle connection during setup.");
                }
                if (FoundryTranslationProtocol.HasType(message, "session.updated"))
                {
                    return;
                }
                if (FoundryTranslationProtocol.HasType(message, "error"))
                {
                    throw new RealtimeTranslationUpstreamException(
                        "Microsoft Foundry rejected the live subtitle configuration.");
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RealtimeTranslationUpstreamException(
                "Microsoft Foundry timed out while configuring live subtitles.");
        }
    }

    private async Task<RelaySessionState> WaitForSessionStartAsync(
        RealtimeTranslationRelayAuthorization authorization,
        CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow().AddSeconds(_options.RelayStartupTimeoutSeconds);
        while (_timeProvider.GetUtcNow() < deadline)
        {
            var state = await LoadSessionStateAsync(authorization, cancellationToken);
            if (state is null || IsTerminal(state.Status) || state.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                throw new RealtimeTranslationExpiredException(
                    "The live subtitle session ended before audio started.");
            }
            if (state.StartedAt is { } startedAt && state.ChargedMinutes >= 1)
            {
                return state;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        throw new RealtimeTranslationExpiredException(
            "The first live subtitle minute was not authorized in time.");
    }

    private async Task MonitorAuthorizationAsync(
        RealtimeTranslationRelayAuthorization authorization,
        DateTimeOffset startedAt,
        RelayBillingState billingState,
        CancellationToken cancellationToken)
    {
        var consecutiveDatabaseFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var now = _timeProvider.GetUtcNow();
                var state = await LoadSessionStateAsync(authorization, cancellationToken);
                if (state is null
                    || IsTerminal(state.Status)
                    || state.ExpiresAt <= now
                    || state.LastHeartbeatAt < now.AddSeconds(-Math.Max(30, _options.StaleSessionSeconds)))
                {
                    throw new RealtimeTranslationExpiredException(
                        "The live subtitle session is no longer authorized.");
                }

                var elapsed = now - startedAt;
                var expectedMinute = Math.Max(1, (int)Math.Floor(elapsed.TotalMinutes) + 1);
                if (expectedMinute > _options.MaxSessionMinutes)
                {
                    throw new RealtimeTranslationExpiredException(
                        "The live subtitle session reached its time limit.");
                }

                var boundary = startedAt.AddMinutes(expectedMinute - 1);
                if (state.ChargedMinutes < expectedMinute
                    && now > boundary.AddSeconds(_options.RelayBillingGraceSeconds))
                {
                    throw new RealtimeTranslationExpiredException(
                        "The current live subtitle minute was not authorized.");
                }

                Volatile.Write(ref billingState.ChargedMinutes, state.ChargedMinutes);
                consecutiveDatabaseFailures = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (RealtimeTranslationExpiredException)
            {
                throw;
            }
            catch (Exception exception)
            {
                consecutiveDatabaseFailures += 1;
                _logger.LogWarning(
                    exception,
                    "Could not verify billing for subtitle relay session {SessionId}",
                    authorization.SessionId);
                if (consecutiveDatabaseFailures >= 3)
                {
                    throw new RealtimeTranslationUpstreamException(
                        "Glosify could not verify the live subtitle session.");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task<RelaySessionState?> LoadSessionStateAsync(
        RealtimeTranslationRelayAuthorization authorization,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<GlosifyContext>();
        return await context.RealtimeTranslationSessions
            .AsNoTracking()
            .Where(session => session.Id == authorization.SessionId
                && session.UserId == authorization.UserId
                && session.TargetLanguage == authorization.TargetLanguage
                && (session.TranscriptId != null) == authorization.SaveTranscript)
            .Select(session => new RelaySessionState(
                session.Status,
                session.StartedAt,
                session.LastHeartbeatAt,
                session.ExpiresAt,
                session.ChargedMinutes,
                session.TranscriptId))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task PumpBrowserToFoundryAsync(
        WebSocket browserSocket,
        WebSocket foundrySocket,
        WebSocket? sourceSocket,
        DateTimeOffset startedAt,
        RelayBillingState billingState,
        CancellationToken cancellationToken)
    {
        long forwardedAudioBytes = 0;
        long sourceUncommittedBytes = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested
                && browserSocket.State == WebSocketState.Open
                && foundrySocket.State == WebSocketState.Open
                && (sourceSocket is null || sourceSocket.State == WebSocketState.Open))
            {
                var message = await ReceiveTextMessageAsync(
                    browserSocket,
                    FoundryTranslationProtocol.MaximumBrowserMessageBytes,
                    cancellationToken);
                if (message is null)
                {
                    return;
                }
                if (!FoundryTranslationProtocol.TryGetBrowserAudioByteCount(
                        message,
                        out var audioByteCount))
                {
                    await browserSocket.CloseOutputAsync(
                        WebSocketCloseStatus.PolicyViolation,
                        "Only bounded subtitle audio messages are accepted.",
                        cancellationToken);
                    return;
                }

                await WaitForAudioCapacityAsync(
                    forwardedAudioBytes + audioByteCount,
                    startedAt,
                    billingState,
                    cancellationToken);

                await foundrySocket.SendAsync(
                    message,
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken);
                if (sourceSocket is not null)
                {
                    await sourceSocket.SendAsync(
                        FoundryTranslationProtocol.CreateSourceAudioAppend(message),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        cancellationToken);
                    sourceUncommittedBytes += audioByteCount;
                    if (sourceUncommittedBytes >= 3L * PcmBytesPerSecond)
                    {
                        await sourceSocket.SendAsync(
                            FoundryTranslationProtocol.CreateSourceAudioCommit(),
                            WebSocketMessageType.Text,
                            endOfMessage: true,
                            cancellationToken);
                        sourceUncommittedBytes = 0;
                    }
                }
                forwardedAudioBytes += audioByteCount;
            }
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested
                && sourceSocket?.State == WebSocketState.Open
                && sourceUncommittedBytes >= PcmBytesPerSecond / 10)
            {
                await sourceSocket.SendAsync(
                    FoundryTranslationProtocol.CreateSourceAudioCommit(),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }

    private async Task WaitForAudioCapacityAsync(
        long requestedAudioBytes,
        DateTimeOffset startedAt,
        RelayBillingState billingState,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var paidBytes = (long)Volatile.Read(ref billingState.ChargedMinutes)
                * 60
                * PcmBytesPerSecond;
            var elapsedSeconds = Math.Max(0, (_timeProvider.GetUtcNow() - startedAt).TotalSeconds);
            // A small transport window keeps ordinary realtime packets smooth,
            // while the paid-minute cap remains absolute.
            var realtimeBytes = (long)((elapsedSeconds + 2) * PcmBytesPerSecond);
            if (requestedAudioBytes <= Math.Min(paidBytes, realtimeBytes))
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
    }

    private async Task PumpFoundryToBrowserAsync(
        WebSocket foundrySocket,
        WebSocket browserSocket,
        SemaphoreSlim browserSendLock,
        Guid sessionId,
        ChannelWriter<CapturedTranslationSegment>? transcriptWriter,
        RelayTranscriptState transcriptState,
        CancellationToken cancellationToken)
    {
        // The translated captions are already on this socket for the live overlay, so
        // saving them alongside the source costs no extra Foundry usage.
        var accumulator = transcriptWriter is null
            ? null
            : new FoundryTranslationTranscriptAccumulator();
        try
        {
            while (!cancellationToken.IsCancellationRequested
                && foundrySocket.State == WebSocketState.Open
                && browserSocket.State == WebSocketState.Open)
            {
                var message = await ReceiveTextMessageAsync(
                    foundrySocket,
                    FoundryTranslationProtocol.MaximumFoundryMessageBytes,
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
                if (!FoundryTranslationProtocol.ShouldForwardFoundryMessage(message))
                {
                    continue;
                }

                await SendBrowserBytesAsync(browserSocket, browserSendLock, message, cancellationToken);
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

    private async Task PumpSourceTranscriptAsync(
        WebSocket sourceSocket,
        WebSocket browserSocket,
        SemaphoreSlim browserSendLock,
        Guid sessionId,
        ChannelWriter<CapturedTranslationSegment>? transcriptWriter,
        RelayTranscriptState transcriptState,
        CancellationToken cancellationToken)
    {
        var accumulator = new FoundrySourceTranscriptAccumulator();
        while (!cancellationToken.IsCancellationRequested
            && sourceSocket.State == WebSocketState.Open)
        {
            var message = await ReceiveTextMessageAsync(
                sourceSocket,
                FoundryTranslationProtocol.MaximumFoundryMessageBytes,
                cancellationToken);
            if (message is null)
            {
                return;
            }
            if (FoundryTranslationProtocol.HasType(message, "error"))
            {
                throw new RealtimeTranslationUpstreamException(
                    "Microsoft Foundry ended source transcription.");
            }

            var segment = accumulator.Apply(message, _timeProvider.GetUtcNow());
            if (segment is not null && transcriptWriter is not null)
            {
                WriteCaption(segment, transcriptWriter, transcriptState, sessionId);
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

    private static bool IsTerminal(string status) =>
        status is RealtimeTranslationSessionStatuses.Completed
            or RealtimeTranslationSessionStatuses.Interrupted
            or RealtimeTranslationSessionStatuses.Failed;

    private sealed record RelaySessionState(
        string Status,
        DateTimeOffset? StartedAt,
        DateTimeOffset LastHeartbeatAt,
        DateTimeOffset ExpiresAt,
        int ChargedMinutes,
        Guid? TranscriptId);

    private sealed class RelayBillingState(int chargedMinutes)
    {
        public int ChargedMinutes = chargedMinutes;
    }

    private sealed class RelayTranscriptState
    {
        public int WarningPending;
    }
}
