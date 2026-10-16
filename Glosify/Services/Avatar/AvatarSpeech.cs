using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Glosify.Services.Speech;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Avatar;

public interface IAvatarSpeech
{
    Task PrepareAsync(AvatarSession session, string mode, CancellationToken ct) => Task.CompletedTask;
    Task<string> ListenAsync(AvatarSession session, string mode, ChannelReader<byte[]> audio,
        Func<string, Task> partial, Func<Task> ready, CancellationToken ct);
    Task SpeakAsync(AvatarSession session, string text, Func<byte[], Task> audio, CancellationToken ct);
}

public sealed class AvatarSpeech(AvatarBilling billing, IOptions<SpeechOptions> options) : IAvatarSpeech, IDisposable
{
    private WebSocket? prepared;
    private string? preparedMode;
    private Task<JsonDocument>? preparedMessage;

    // No audio, reservations or generation requests are submitted during preparation.
    public async Task PrepareAsync(AvatarSession session, string mode, CancellationToken ct)
    {
        DiscardPrepared();
        // Preparation is optional, so a slow provider gives up early. A turn's own
        // connect keeps the turn deadline instead.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        prepared = await ConnectRecognitionAsync(session, mode, deadline.Token);
        preparedMode = mode;
        // Observe provider idle errors/closure before handing the socket to a turn.
        preparedMessage = AvatarWire.ReadAsync(prepared, ct);
    }

    public void Dispose() => DiscardPrepared();

    private void DiscardPrepared()
    {
        prepared?.Dispose(); prepared = null;
        _ = DiscardReadAsync(preparedMessage); preparedMessage = null;
    }

    private static async Task DiscardReadAsync(Task<JsonDocument>? read)
    {
        if (read is null) return;
        try { using var message = await read; }
        catch (Exception) { /* Socket disposal cancels pending reads; always observe them. */ }
    }

    private async Task<WebSocket> ConnectRecognitionAsync(AvatarSession session, string mode, CancellationToken ct)
    {
        var socket = Socket();
        try
        {
            var strategy = mode == "hands-free" ? "vad" : "manual";
            var url = $"wss://api.elevenlabs.io/v1/speech-to-text/realtime?model_id=scribe_v2_realtime&audio_format=pcm_16000&language_code={Uri.EscapeDataString(session.Language.ScribeCode)}&commit_strategy={strategy}";
            await socket.ConnectAsync(new Uri(url), ct);
            using var start = await AvatarWire.ReadAsync(socket, ct);
            if (start.RootElement.GetProperty("message_type").GetString() != "session_started") throw Unavailable();
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }

    public async Task<string> ListenAsync(AvatarSession session, string mode, ChannelReader<byte[]> audio,
        Func<string, Task> partial, Func<Task> ready, CancellationToken ct)
    {
        var reservation = await billing.ReserveAsync(session.UserId, session.Id, "recognition", 45, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        if (preparedMode != mode || preparedMessage?.IsCompleted == true) DiscardPrepared();
        var connection = prepared; var receiving = preparedMessage;
        prepared = null; preparedMessage = null;
        try
        {
            return await RecognizeAsync(connection, receiving, token => ConnectRecognitionAsync(session, mode, token), audio, mode,
                (seconds, token) => billing.SubmittedAsync(reservation, seconds, token), partial, ready, timeout.Token);
        }
        catch (Exception ex) when (ex is WebSocketException && timeout.IsCancellationRequested)
        {
            throw new OperationCanceledException(timeout.Token);
        }
        finally { await billing.SettleAsync(reservation, CancellationToken.None); }
    }

    // The provider closes a session without enough audio activity, so an idle
    // prepared session can fail just after it passed the check before a turn.
    // Until it returns a transcript, the turn keeps its audio and replays it
    // once on a fresh session instead of losing the learner's first words.
    internal static async Task<string> RecognizeAsync(WebSocket? connection, Task<JsonDocument>? receiving,
        Func<CancellationToken, Task<WebSocket>> connect, ChannelReader<byte[]> audio, string mode,
        Func<decimal, CancellationToken, Task> submitted, Func<string, Task> partial, Func<Task> ready, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var sending = new SemaphoreSlim(1);
        List<(byte[] Chunk, bool Commit)>? replay = connection is null ? null : [];
        var abort = default(CancellationTokenRegistration);
        Task? sender = null;
        try
        {
            connection ??= await connect(stop.Token);
            // The prepared read began under the connection lifetime. Also enforce
            // this turn's deadline and interruption while waiting for that read.
            abort = stop.Token.Register(connection.Abort);
            sender = SendAudioAsync(audio, mode, submitted, async (chunk, commit, token) =>
            {
                await sending.WaitAsync(token);
                try
                {
                    replay?.Add((chunk, commit));
                    try { await SendChunkAsync(connection!, chunk, commit, token); }
                    catch (Exception ex) when (replay is not null && !token.IsCancellationRequested
                        && ex is WebSocketException or OperationCanceledException)
                    { connection!.Abort(); } // The receive loop replays this chunk on a fresh session.
                }
                finally { sending.Release(); }
            }, stop.Token);
            await ready();
            while (true)
            {
                var read = receiving ??= AvatarWire.ReadAsync(connection, stop.Token);
                if (await Task.WhenAny(read, sender) == sender) await sender;
                JsonDocument? message = null;
                try { message = await read; }
                catch (Exception ex) when (replay is not null && !stop.IsCancellationRequested
                    && ex is WebSocketException or OperationCanceledException) { }
                receiving = null;
                using (message)
                {
                    var type = message?.RootElement.GetProperty("message_type").GetString() ?? "error";
                    if (type.Contains("error", StringComparison.OrdinalIgnoreCase))
                    {
                        if (replay is null) throw Unavailable();
                        await ReplaceAsync();
                        continue;
                    }
                    var text = message!.RootElement.TryGetProperty("text", out var item) ? item.GetString() ?? "" : "";
                    if (text.Length > 4000) throw new AvatarException(400, "Please try a shorter sentence.");
                    if (type is "partial_transcript" or "committed_transcript" && replay is not null)
                    {
                        // A transcript proves the session is serving this turn.
                        await sending.WaitAsync(stop.Token);
                        replay = null; sending.Release();
                    }
                    if (type == "partial_transcript") await partial(text);
                    if (type == "committed_transcript") return text.Trim();
                }
            }
        }
        finally
        {
            stop.Cancel(); connection?.Abort();
            if (sender is not null) { try { await sender; } catch (Exception) { /* The receive loop observes sender failure; always settle in finally. */ } }
            await DiscardReadAsync(receiving);
            abort.Dispose();
            connection?.Dispose();
        }

        async Task ReplaceAsync()
        {
            // A pending send on the failed session completes (and is kept) once aborted.
            connection!.Abort();
            var fresh = await connect(stop.Token);
            try { await sending.WaitAsync(stop.Token); }
            catch { fresh.Dispose(); throw; }
            try
            {
                abort.Dispose(); connection.Dispose();
                connection = fresh; abort = stop.Token.Register(fresh.Abort);
                foreach (var (chunk, commit) in replay!) await SendChunkAsync(fresh, chunk, commit, stop.Token);
                replay = null; // Retry once; a fresh session's failure is reported.
            }
            finally { sending.Release(); }
        }
    }

    private static Task SendChunkAsync(WebSocket socket, byte[] chunk, bool commit, CancellationToken ct) =>
        AvatarWire.SendAsync(socket, new { message_type = "input_audio_chunk", audio_base_64 = Convert.ToBase64String(chunk), sample_rate = 16000, commit }, ct);

    internal static async Task SendAudioAsync(ChannelReader<byte[]> audio, string mode,
        Func<decimal, CancellationToken, Task> submitted,
        Func<byte[], bool, CancellationToken, Task> send, CancellationToken ct)
    {
        long bytes = 0;
        await foreach (var chunk in audio.ReadAllAsync(ct))
        {
            if (chunk.Length == 0) break;
            bytes += chunk.Length;
            if (bytes > 45 * 32000) throw new AvatarException(400, mode == "hands-free"
                ? "Listening paused after 45 seconds without a completed utterance. Please retry the microphone."
                : "Please limit each turn to 45 seconds.");
            await submitted(bytes / 32000m, ct);
            await send(chunk, false, ct);
        }
        // Completion cannot be dropped when the bounded microphone queue is full.
        // Drain every accepted frame before committing a push-to-talk utterance.
        ct.ThrowIfCancellationRequested();
        if (mode == "push-to-talk") await send([], true, ct);
    }

    public async Task SpeakAsync(AvatarSession session, string text, Func<byte[], Task> audio, CancellationToken ct)
    {
        if (text.Length is 0 or > 4000) throw new AvatarException(502, "The avatar reply was too long. Try a shorter question.");
        var voice = session.Voice ?? throw new AvatarException(503, "Please start a new conversation to select a voice for your language.");
        var reservation = await billing.ReserveAsync(session.UserId, session.Id, "speech", text.Length, ct);
        using var socket = Socket();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            await socket.ConnectAsync(SpeechUri(session.Language), timeout.Token);
            await AvatarWire.SendAsync(socket, new { voices = new[] { voice.Id } }, timeout.Token);
            await billing.SubmittedAsync(reservation, text.Length, timeout.Token);
            await AvatarWire.SendAsync(socket, new { inputs = new[] { new { text, voice_id = voice.Id, new_turn = true } } }, timeout.Token);
            await AvatarWire.SendAsync(socket, new { close_socket = true }, timeout.Token);
            var bytes = 0;
            while (true)
            {
                using var message = await AvatarWire.ReadAsync(socket, timeout.Token, 2 * 1024 * 1024);
                var root = message.RootElement;
                if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null) throw Unavailable();
                if (root.TryGetProperty("audio", out var encoded) && encoded.GetString() is { Length: > 0 } value)
                {
                    var chunk = Convert.FromBase64String(value);
                    bytes += chunk.Length;
                    if (bytes > 8 * 1024 * 1024) throw Unavailable();
                    await audio(chunk);
                }
                if (root.TryGetProperty("is_final", out var final) && final.ValueKind == JsonValueKind.True) break;
            }
            if (bytes == 0) throw Unavailable();
        }
        finally { socket.Abort(); await billing.SettleAsync(reservation, CancellationToken.None); }
    }
    internal static Uri SpeechUri(Glosify.Services.Language.QuizLanguage language) =>
        new($"wss://api.elevenlabs.io/v1/text-to-dialogue/stream-input?model_id={AvatarOptions.SpeechModel}&output_format=pcm_24000&language_code={Uri.EscapeDataString(AvatarVoices.SpeechLanguage(language))}");
    private ClientWebSocket Socket()
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("xi-api-key", options.Value.ApiKey);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
        return socket;
    }
    private static AvatarException Unavailable() => new(502, "Voice is temporarily unavailable. Please retry.");
}

internal static class AvatarWire
{
    public static async Task SendAsync(WebSocket socket, object value, CancellationToken ct) =>
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value), WebSocketMessageType.Text, true, ct);
    public static async Task<JsonDocument> ReadAsync(WebSocket socket, CancellationToken ct, int max = 65536)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult read;
        do
        {
            read = await socket.ReceiveAsync(buffer, ct);
            if (read.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Voice connection closed.");
            if (memory.Length + read.Count > max) throw new AvatarException(400, "Voice message is too large.");
            memory.Write(buffer, 0, read.Count);
        } while (!read.EndOfMessage);
        return JsonDocument.Parse(memory.ToArray());
    }
}
