using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glosify.Services.RealtimeTranslation;

public interface IEconomicalTranslationRelay
{
    Task RelayAsync(
        WebSocket browserSocket,
        RealtimeTranslationRelayAuthorization authorization,
        CancellationToken cancellationToken = default);
}

public sealed class EconomicalTranslationRelay : IEconomicalTranslationRelay
{
    private const int PcmBytesPerSecond = 24_000 * sizeof(short);
    private readonly IRealtimeSpeechTranscriber _speech;
    private readonly IEconomicalSubtitleTranslator _translator;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RealtimeTranslationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EconomicalTranslationRelay> _logger;

    public EconomicalTranslationRelay(
        IRealtimeSpeechTranscriber speech,
        IEconomicalSubtitleTranslator translator,
        IServiceScopeFactory scopeFactory,
        IOptions<RealtimeTranslationOptions> options,
        TimeProvider timeProvider,
        ILogger<EconomicalTranslationRelay> logger)
    {
        _speech = speech;
        _translator = translator;
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
        if (!_options.EconomicalEnabled
            || authorization.TranslationMode != RealtimeTranslationModes.Economical
            || string.IsNullOrWhiteSpace(authorization.SourceLanguage))
        {
            throw new RealtimeTranslationUnavailableException(
                "Economical subtitles are not configured on this Glosify deployment.");
        }

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
        Task? transcriptWriter = null;

        try
        {
            await FoundryTranslationRelay.SendBrowserControlAsync(
                browserSocket,
                "glosify.relay.ready",
                null,
                relayToken);
            var session = await WaitForSessionStartAsync(authorization, relayToken);
            var billing = new RelayBillingState(session.ChargedMinutes);
            if (transcripts is not null)
            {
                transcriptWriter = WriteTranscriptsAsync(
                    authorization.SessionId,
                    transcripts.Reader,
                    CancellationToken.None);
            }

            var browserPump = PumpBrowserAudioAsync(
                browserSocket,
                audio.Writer,
                session.StartedAt!.Value,
                billing,
                relayToken);
            var speechPump = _speech.TranscribeAsync(
                authorization.SourceLanguage,
                audio.Reader,
                recognized.Writer,
                relayToken);
            var translationPump = TranslateAndSendAsync(
                browserSocket,
                authorization,
                recognized.Reader,
                transcripts?.Writer,
                relayToken);
            var authorizationMonitor = MonitorAuthorizationAsync(
                authorization,
                session.StartedAt.Value,
                billing,
                relayToken);

            var completed = await Task.WhenAny(
                browserPump,
                speechPump,
                translationPump,
                authorizationMonitor);
            if (completed == browserPump)
            {
                await browserPump;
                audio.Writer.TryComplete();
                await speechPump;
                await translationPump;
            }
            else
            {
                await completed;
                if (!relayToken.IsCancellationRequested)
                {
                    throw new RealtimeTranslationUpstreamException(
                        "Economical subtitles ended unexpectedly.");
                }
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
                "Economical subtitle transport failed for session {SessionId}",
                authorization.SessionId);
            throw new RealtimeTranslationUpstreamException(
                "The economical subtitle connection ended.");
        }
        catch (Exception exception)
        {
            RealtimeTranslationTelemetry.UpstreamFailures.Add(1);
            _logger.LogWarning(
                exception,
                "Economical subtitle provider failed for session {SessionId}",
                authorization.SessionId);
            throw new RealtimeTranslationUpstreamException(
                "A Microsoft service ended economical subtitles.");
        }
        finally
        {
            relayCancellation.Cancel();
            audio.Writer.TryComplete();
            recognized.Writer.TryComplete();
            transcripts?.Writer.TryComplete();
            if (transcriptWriter is not null)
            {
                try
                {
                    await transcriptWriter.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning(
                        "Timed out while flushing economical captions for session {SessionId}",
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
        RelayBillingState billing,
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
                if (!FoundryTranslationProtocol.TryDecodeBrowserAudio(message, out var bytes))
                {
                    await browserSocket.CloseOutputAsync(
                        WebSocketCloseStatus.PolicyViolation,
                        "Only bounded subtitle audio messages are accepted.",
                        cancellationToken);
                    return;
                }
                await WaitForAudioCapacityAsync(
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
        CancellationToken cancellationToken)
    {
        await foreach (var segment in recognized.ReadAllAsync(cancellationToken))
        {
            var result = await _translator.TranslateAsync(
                segment,
                authorization.TargetLanguage,
                cancellationToken);

            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "glosify.translation.segment",
                sequence = segment.Sequence,
                sourceLanguage = result.SourceLanguage,
                targetLanguage = result.TargetLanguage,
                text = result.TranslatedText,
            });
            await browserSocket.SendAsync(
                payload,
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
            RealtimeTranslationTelemetry.TranslatedCharacters.Add(result.SourceText.Length);

            if (transcripts is not null)
            {
                await transcripts.WriteAsync(new CapturedTranslationSegment(
                    segment.Sequence,
                    $"economical:source:{segment.Sequence}",
                    result.SourceText,
                    result.CapturedAt), cancellationToken);
                await transcripts.WriteAsync(new CapturedTranslationSegment(
                    segment.Sequence,
                    $"economical:translation:{segment.Sequence}",
                    result.TranslatedText,
                    result.CapturedAt,
                    RealtimeTranslationTranscriptStreams.Translation), cancellationToken);
            }
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
            if (state.StartedAt is not null && state.ChargedMinutes >= 1)
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
        RelayBillingState billing,
        CancellationToken cancellationToken)
    {
        var databaseFailures = 0;
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
                var expectedMinute = Math.Max(1, (int)Math.Floor((now - startedAt).TotalMinutes) + 1);
                var boundary = startedAt.AddMinutes(expectedMinute - 1);
                if (expectedMinute > _options.MaxSessionMinutes
                    || (state.ChargedMinutes < expectedMinute
                        && now > boundary.AddSeconds(_options.RelayBillingGraceSeconds)))
                {
                    throw new RealtimeTranslationExpiredException(
                        "The current live subtitle minute was not authorized.");
                }
                Volatile.Write(ref billing.ChargedMinutes, state.ChargedMinutes);
                databaseFailures = 0;
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
                databaseFailures++;
                _logger.LogWarning(
                    exception,
                    "Could not verify economical subtitle billing for session {SessionId}",
                    authorization.SessionId);
                if (databaseFailures >= 3)
                {
                    throw new RealtimeTranslationUpstreamException(
                        "Glosify could not verify the live subtitle session.");
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task WaitForAudioCapacityAsync(
        long requestedBytes,
        DateTimeOffset startedAt,
        RelayBillingState billing,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var paidBytes = (long)Volatile.Read(ref billing.ChargedMinutes) * 60 * PcmBytesPerSecond;
            var elapsedSeconds = Math.Max(0, (_timeProvider.GetUtcNow() - startedAt).TotalSeconds);
            var realtimeBytes = (long)((elapsedSeconds + 2) * PcmBytesPerSecond);
            if (requestedBytes <= Math.Min(paidBytes, realtimeBytes))
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
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
                && session.TranslationMode == authorization.TranslationMode
                && session.SourceLanguage == authorization.SourceLanguage
                && (session.TranscriptId != null) == authorization.SaveTranscript)
            .Select(session => new RelaySessionState(
                session.Status,
                session.StartedAt,
                session.LastHeartbeatAt,
                session.ExpiresAt,
                session.ChargedMinutes))
            .SingleOrDefaultAsync(cancellationToken);
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
                    "Could not store economical captions for session {SessionId}",
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
                || message.Length + result.Count > FoundryTranslationProtocol.MaximumBrowserMessageBytes)
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

    private static bool IsTerminal(string status) =>
        status is RealtimeTranslationSessionStatuses.Completed
            or RealtimeTranslationSessionStatuses.Interrupted
            or RealtimeTranslationSessionStatuses.Failed;

    private sealed record RelaySessionState(
        string Status,
        DateTimeOffset? StartedAt,
        DateTimeOffset LastHeartbeatAt,
        DateTimeOffset ExpiresAt,
        int ChargedMinutes);

    private sealed class RelayBillingState(int chargedMinutes)
    {
        public int ChargedMinutes = chargedMinutes;
    }
}
