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
    private readonly RealtimeTranslationRelayAuthorizationMonitor _authorizationMonitor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RealtimeTranslationOptions _options;
    private readonly ILogger<ScribeTranslationRelay> _logger;

    public ScribeTranslationRelay(
        IRealtimeSpeechTranscriber speech,
        IEconomicalSubtitleTranslator translator,
        RealtimeTranslationRelayAuthorizationMonitor authorizationMonitor,
        IServiceScopeFactory scopeFactory,
        IOptions<RealtimeTranslationOptions> options,
        ILogger<ScribeTranslationRelay> logger)
    {
        _speech = speech;
        _translator = translator;
        _authorizationMonitor = authorizationMonitor;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task RelayAsync(
        WebSocket browserSocket,
        RealtimeTranslationRelayAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        var supportedMode = authorization.TranslationMode == RealtimeTranslationModes.Scribe
            && _options.ElevenLabs.Enabled;
        if (!supportedMode
            || string.IsNullOrWhiteSpace(authorization.SourceLanguage))
        {
            throw new RealtimeTranslationUnavailableException(
                "The selected speech recognition mode is not configured on this Glosify deployment.");
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
                relayToken);
            var translationPump = TranslateAndSendAsync(
                browserSocket,
                authorization,
                recognized.Reader,
                transcripts?.Writer,
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
            if (transcriptWriter is not null)
            {
                try
                {
                    await transcriptWriter.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning(
                        "Timed out while flushing speech-recognition captions for session {SessionId}",
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
                if (!FoundryTranslationProtocol.TryDecodeBrowserAudio(message, out var bytes))
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

}
