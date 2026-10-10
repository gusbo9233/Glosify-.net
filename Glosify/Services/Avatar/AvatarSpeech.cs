using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Glosify.Services.Speech;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Avatar;

public interface IAvatarSpeech
{
    Task<string> ListenAsync(AvatarSession session, string mode, ChannelReader<byte[]> audio,
        Func<string, Task> partial, Func<Task> ready, CancellationToken ct);
    Task SpeakAsync(AvatarSession session, string text, Func<byte[], Task> audio, CancellationToken ct);
}

public sealed class AvatarSpeech(AvatarBilling billing, IOptions<SpeechOptions> options) : IAvatarSpeech
{
    public async Task<string> ListenAsync(AvatarSession session, string mode, ChannelReader<byte[]> audio,
        Func<string, Task> partial, Func<Task> ready, CancellationToken ct)
    {
        var reservation = await billing.ReserveAsync(session.UserId, session.Id, "recognition", 45, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var socket = Socket();
        Task? sender = null;
        try
        {
            var strategy = mode == "hands-free" ? "vad" : "manual";
            var url = $"wss://api.elevenlabs.io/v1/speech-to-text/realtime?model_id=scribe_v2_realtime&audio_format=pcm_16000&language_code={Uri.EscapeDataString(session.Language.ScribeCode)}&commit_strategy={strategy}";
            await socket.ConnectAsync(new Uri(url), timeout.Token);
            using var start = await AvatarWire.ReadAsync(socket, timeout.Token);
            if (start.RootElement.GetProperty("message_type").GetString() != "session_started") throw Unavailable();
            sender = SendAudioAsync(audio, mode,
                (seconds, token) => billing.SubmittedAsync(reservation, seconds, token),
                (chunk, commit, token) => AvatarWire.SendAsync(socket, new { message_type = "input_audio_chunk", audio_base_64 = Convert.ToBase64String(chunk), sample_rate = 16000, commit }, token),
                timeout.Token);
            await ready();
            while (true)
            {
                var read = AvatarWire.ReadAsync(socket, timeout.Token);
                if (await Task.WhenAny(read, sender) == sender) await sender;
                using var message = await read;
                var type = message.RootElement.GetProperty("message_type").GetString() ?? "";
                if (type.Contains("error", StringComparison.OrdinalIgnoreCase)) throw Unavailable();
                var text = message.RootElement.TryGetProperty("text", out var item) ? item.GetString() ?? "" : "";
                if (text.Length > 4000) throw new AvatarException(400, "Please try a shorter sentence.");
                if (type == "partial_transcript") await partial(text);
                if (type == "committed_transcript") return text.Trim();
            }
        }
        finally
        {
            timeout.Cancel(); socket.Abort();
            if (sender is not null) { try { await sender; } catch (Exception) { /* The receive loop observes sender failure; always settle in finally. */ } }
            await billing.SettleAsync(reservation, CancellationToken.None);
        }
    }

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
