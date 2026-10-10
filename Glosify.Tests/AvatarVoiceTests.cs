using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Avatar;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Glosify.Tests;

public sealed class AvatarVoiceTests
{
    [Theory]
    [InlineData("push-to-talk")]
    [InlineData("hands-free")]
    public async Task AudioProducesCaptionsAndReplyAndPlaybackAcknowledgementCommitsMemory(string mode)
    {
        var ai = new Reply();
        using var app = App(ai);
        using var client = await app.Client("admin"); await app.Antiforgery(client);
        using var start = await client.PostAsJsonAsync("/api/avatar/sessions", new { language = "sv" });
        var id = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetGuid();
        var ws = app.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request =>
        {
            request.Headers["Cookie"] = client.DefaultRequestHeaders.GetValues("Cookie").Single();
            request.Headers["Origin"] = "https://localhost";
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await ws.ConnectAsync(new Uri($"wss://localhost/api/avatar/sessions/{id}/voice"), timeout.Token);
        await Until(socket, "connected", timeout.Token);
        await AvatarWire.SendAsync(socket, new { type = "listen", mode }, timeout.Token);
        var listening = await Until(socket, "listening", timeout.Token);
        var turnId = listening.GetProperty("turnId").GetString();
        // Exercise fragmented WebSocket frames, as seen over real networks.
        await socket.SendAsync(new byte[3200], WebSocketMessageType.Binary, false, timeout.Token);
        await socket.SendAsync(new byte[3200], WebSocketMessageType.Binary, true, timeout.Token);
        if (mode == "push-to-talk") await AvatarWire.SendAsync(socket, new { type = "commit" }, timeout.Token);
        var caption = await Until(socket, "transcript", timeout.Token);
        Assert.Equal("Hej!", caption.GetProperty("text").GetString());
        var reply = await Until(socket, "reply", timeout.Token);
        Assert.Equal("Hej! Hur mår du?", reply.GetProperty("text").GetString());
        await Until(socket, "audio-end", timeout.Token);
        var session = app.Services.GetRequiredService<AvatarSessions>().Get(id, "admin");
        Assert.Single(session.History); // Undelivered assistant text cannot become remembered speech.
        await AvatarWire.SendAsync(socket, new { type = "played", turnId }, timeout.Token);
        await Until(socket, "idle", timeout.Token);
        Assert.Equal(2, session.History.Count);
        Assert.Contains("Swedish", ai.LastRequest!.SystemInstruction);
        Assert.Empty(ai.LastRequest.Tools);
        await AvatarWire.SendAsync(socket, new { type = "end" }, timeout.Token);
        await socket.ReceiveAsync(new byte[4096], timeout.Token);
    }

    [Fact]
    public async Task InterruptionCancelsGenerationAndDoesNotRememberUnspokenReply()
    {
        var ai = new Reply { Wait = true }; using var app = App(ai);
        using var client = await app.Client("admin"); await app.Antiforgery(client);
        using var start = await client.PostAsJsonAsync("/api/avatar/sessions", new { language = "en" });
        var id = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetGuid();
        var ws = app.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request => { request.Headers["Cookie"] = client.DefaultRequestHeaders.GetValues("Cookie").Single(); request.Headers["Origin"] = "https://localhost"; };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await ws.ConnectAsync(new Uri($"wss://localhost/api/avatar/sessions/{id}/voice"), timeout.Token);
        await Until(socket, "connected", timeout.Token);
        await AvatarWire.SendAsync(socket, new { type = "listen", mode = "hands-free" }, timeout.Token);
        await Until(socket, "listening", timeout.Token);
        await socket.SendAsync(new byte[6400], WebSocketMessageType.Binary, true, timeout.Token);
        await Until(socket, "thinking", timeout.Token);
        await AvatarWire.SendAsync(socket, new { type = "interrupt" }, timeout.Token);
        await Until(socket, "idle", timeout.Token);
        Assert.True(ai.Cancelled);
        Assert.Single(app.Services.GetRequiredService<AvatarSessions>().Get(id, "admin").History);
        await AvatarWire.SendAsync(socket, new { type = "end" }, timeout.Token);
    }

    private static AvatarFixture App(Reply reply) => new()
    {
        Overrides = services =>
        {
            services.RemoveAll<IAvatarSpeech>(); services.AddSingleton<IAvatarSpeech, Speech>();
            services.RemoveAll<IGenerativeAiClient>(); services.AddSingleton<IGenerativeAiClient>(reply);
        }
    };
    private static async Task<JsonElement> Until(WebSocket socket, string type, CancellationToken ct)
    {
        while (true)
        {
            using var doc = await AvatarWire.ReadAsync(socket, ct, 3 * 1024 * 1024);
            var root = doc.RootElement;
            Assert.NotEqual("error", root.GetProperty("type").GetString());
            if (root.GetProperty("type").GetString() == type) return root.Clone();
        }
    }
    private sealed class Speech : IAvatarSpeech
    {
        public async Task<string> ListenAsync(AvatarSession session, string mode, ChannelReader<byte[]> audio, Func<string, Task> partial, Func<Task> ready, CancellationToken ct)
        {
            await ready();
            await foreach (var chunk in audio.ReadAllAsync(ct))
                if (chunk.Length == 0 || mode == "hands-free") return "Hej!";
            return "";
        }
        public Task SpeakAsync(AvatarSession session, string text, Func<byte[], Task> audio, CancellationToken ct)
        {
            var bytes = new byte[96000];
            for (var i = 0; i < bytes.Length / 2; i++)
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2),
                    (short)(Math.Sin(i / 24000d * Math.PI * 2 * 170) * 6500 * (.5 + .5 * Math.Sin(i / 24000d * 18))));
            return audio(bytes);
        }
    }
    private sealed class Reply : IGenerativeAiClient
    {
        public AgentRequest? LastRequest;
        public bool Wait;
        public bool Cancelled;
        public async Task<AgentTurnResult> RunAgentTurnAsync(AgentRequest request, AiUsageContext context, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            try { if (Wait) await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return new("Hej! Hur mår du?", []);
        }
        public Task<T> GenerateStructuredAsync<T>(string prompt, AiUsageContext context, string? model = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ExtractTextFromImageAsync(byte[] image, string contentType, string prompt, AiUsageContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
