using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Glosify.Data;
using Glosify.Services.Abuse;
using Microsoft.EntityFrameworkCore;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Avatar;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
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
        await Until(socket, "usage", timeout.Token);
        await AssertNoReservations(app);
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
        await ai.Started.Task.WaitAsync(timeout.Token);
        await app.Seed(async db => Assert.Single(await db.Set<ResourceReservation>().ToListAsync()));
        await AvatarWire.SendAsync(socket, new { type = "interrupt" }, timeout.Token);
        await Until(socket, "idle", timeout.Token);
        Assert.True(ai.Cancelled);
        await AssertNoReservations(app);
        Assert.Single(app.Services.GetRequiredService<AvatarSessions>().Get(id, "admin").History);
        await AvatarWire.SendAsync(socket, new { type = "end" }, timeout.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TurnDeadlineReportsRetryWhileDisconnectCancelsQuietly(bool disconnect)
    {
        var ai = new Reply { Wait = true };
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var logger = new CapturedLogger();
        using var app = App(ai);
        var configure = app.Overrides!;
        app.Overrides = services =>
        {
            configure(services);
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<ILogger<AvatarConversation>>(logger);
        };
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
        await ai.Started.Task.WaitAsync(timeout.Token);
        await app.Seed(async db => Assert.Single(await db.Set<ResourceReservation>().ToListAsync()));
        if (disconnect)
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Leaving", timeout.Token);
            while ((await socket.ReceiveAsync(new byte[65536], timeout.Token)).MessageType != WebSocketMessageType.Close) { }
        }
        else
        {
            clock.Advance(TimeSpan.FromSeconds(121));
            var error = await Until(socket, "error", timeout.Token);
            Assert.Equal(504, error.GetProperty("status").GetInt32());
            Assert.Contains("retry", error.GetProperty("detail").GetString());
            // The same socket remains usable after the timed-out turn.
            ai.Wait = false;
            await AvatarWire.SendAsync(socket, new { type = "listen", mode = "hands-free" }, timeout.Token);
            await Until(socket, "listening", timeout.Token);
            await socket.SendAsync(new byte[6400], WebSocketMessageType.Binary, true, timeout.Token);
            await Until(socket, "reply", timeout.Token);
            await AvatarWire.SendAsync(socket, new { type = "end" }, timeout.Token);
            while ((await socket.ReceiveAsync(new byte[65536], timeout.Token)).MessageType != WebSocketMessageType.Close) { }
        }
        Assert.True(ai.Cancelled);
        await AssertNoReservations(app);
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public async Task ProviderFailureReleasesTurnStorageReservation()
    {
        var ai = new Reply { Fail = true }; using var app = App(ai);
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
        var error = await Until(socket, "error", timeout.Token);
        Assert.Equal(502, error.GetProperty("status").GetInt32());
        Assert.True(ai.Started.Task.IsCompletedSuccessfully);
        await Until(socket, "usage", timeout.Token);
        await AssertNoReservations(app);
        await AvatarWire.SendAsync(socket, new { type = "end" }, timeout.Token);
    }

    private sealed class CapturedLogger : ILogger<AvatarConversation>
    {
        public ConcurrentQueue<string> Warnings { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (level >= LogLevel.Warning) Warnings.Enqueue(formatter(state, exception)); }
    }

    private static AvatarFixture App(Reply reply) => new()
    {
        Overrides = services =>
        {
            services.RemoveAll<IAvatarSpeech>(); services.AddSingleton<IAvatarSpeech, Speech>();
            services.RemoveAll<IGenerativeAiClient>();
            services.AddScoped<IGenerativeAiClient>(sp => new ReservingReply(reply, sp.GetRequiredService<ResourceQuotaService>(), sp.GetRequiredService<RequestResourceReservations>()));
        }
    };
    private static async Task<JsonElement> Until(WebSocket socket, string type, CancellationToken ct)
    {
        while (true)
        {
            using var doc = await AvatarWire.ReadAsync(socket, ct, 3 * 1024 * 1024);
            var root = doc.RootElement;
            if (type != "error") Assert.NotEqual("error", root.GetProperty("type").GetString());
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
            return "Hej!";
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
    private static Task AssertNoReservations(AvatarFixture app) => app.Seed(async db =>
        Assert.Empty(await db.Set<ResourceReservation>().ToListAsync()));

    private sealed class ReservingReply(Reply reply, ResourceQuotaService quotas, RequestResourceReservations reservations) : IGenerativeAiClient
    {
        public async Task<AgentTurnResult> RunAgentTurnAsync(AgentRequest request, AiUsageContext context, CancellationToken cancellationToken = default)
        {
            var id = await quotas.ReserveAsync("admin", new() { ["content_bytes"] = 1024 * 1024 }, cancellationToken);
            reservations.Track(id, "admin");
            return await reply.RunAgentTurnAsync(request, context, cancellationToken);
        }
        public Task<T> GenerateStructuredAsync<T>(string prompt, AiUsageContext context, string? model = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ExtractTextFromImageAsync(byte[] image, string contentType, string prompt, AiUsageContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Reply : IGenerativeAiClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AgentRequest? LastRequest;
        public bool Wait;
        public bool Fail;
        public bool Cancelled;
        public async Task<AgentTurnResult> RunAgentTurnAsync(AgentRequest request, AiUsageContext context, CancellationToken cancellationToken = default)
        {
            LastRequest = request; Started.TrySetResult();
            if (Fail) throw new InvalidOperationException("Provider unavailable");
            try { if (Wait) await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return new("Hej! Hur mår du?", []);
        }
        public Task<T> GenerateStructuredAsync<T>(string prompt, AiUsageContext context, string? model = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ExtractTextFromImageAsync(byte[] image, string contentType, string prompt, AiUsageContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
