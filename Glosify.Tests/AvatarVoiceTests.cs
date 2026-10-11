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
    [InlineData("push-to-talk", "rain", "Rain")]
    [InlineData("hands-free", "rowan", "Rowan")]
    public async Task AudioProducesCaptionsAndReplyAndPlaybackAcknowledgementCommitsMemory(string mode, string characterId, string name)
    {
        var ai = new Reply();
        using var app = App(ai);
        using var client = await app.Client("admin"); await app.Antiforgery(client);
        using var start = await client.PostAsJsonAsync("/api/avatar/sessions", new { language = "sv", characterId });
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
        Assert.Contains("You are " + name + ",", ai.LastRequest.SystemInstruction);
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
            for (var i = 0; i < 3; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(30));
                await AvatarWire.SendAsync(socket, new { type = "ping" }, timeout.Token);
                await Until(socket, "pong", timeout.Token);
            }
            clock.Advance(TimeSpan.FromSeconds(31));
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
        Assert.Equal(1, ai.Calls);
        ai.Fail = false;
        await AvatarWire.SendAsync(socket, new { type = "listen", mode = "hands-free" }, timeout.Token);
        await Until(socket, "listening", timeout.Token);
        await socket.SendAsync(new byte[6400], WebSocketMessageType.Binary, true, timeout.Token);
        await Until(socket, "audio-end", timeout.Token);
        Assert.Equal(2, ai.Calls);
        await AvatarWire.SendAsync(socket, new { type = "end" }, timeout.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectRetainsContextAndReconnectRequiresANewExplicitTurn(bool duringReply)
    {
        var ai = new Reply { Wait = duringReply }; using var app = App(ai);
        using var client = await app.Client("admin"); await app.Antiforgery(client);
        using var start = await client.PostAsJsonAsync("/api/avatar/sessions", new { });
        var id = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetGuid();
        var sessions = app.Services.GetRequiredService<AvatarSessions>();
        var session = sessions.Get(id, "admin");
        var ws = app.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request => { request.Headers["Cookie"] = client.DefaultRequestHeaders.GetValues("Cookie").Single(); request.Headers["Origin"] = "https://localhost"; };
        var url = new Uri($"wss://localhost/api/avatar/sessions/{id}/voice");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var first = await ws.ConnectAsync(url, timeout.Token);
        await Until(first, "connected", timeout.Token);
        await AvatarWire.SendAsync(first, new { type = "listen", mode = "hands-free" }, timeout.Token);
        var listening = await Until(first, "listening", timeout.Token);
        await first.SendAsync(new byte[6400], WebSocketMessageType.Binary, true, timeout.Token);
        await ai.Started.Task.WaitAsync(timeout.Token);
        if (!duringReply)
        {
            await Until(first, "audio-end", timeout.Token);
            await AvatarWire.SendAsync(first, new { type = "played", turnId = listening.GetProperty("turnId").GetString() }, timeout.Token);
            await Until(first, "idle", timeout.Token);
        }
        await first.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "Network lost", timeout.Token);
        while ((await first.ReceiveAsync(new byte[65536], timeout.Token)).MessageType != WebSocketMessageType.Close) { }
        while (Volatile.Read(ref session.Connected) != 0) await Task.Delay(5, timeout.Token);
        Assert.Equal(duringReply, ai.Cancelled);
        await AssertNoReservations(app);
        var priorCount = duringReply ? 1 : 2;
        Assert.Equal(priorCount, session.History.Count);
        ai.Wait = false;
        using var resumed = await ws.ConnectAsync(url, timeout.Token);
        await Until(resumed, "connected", timeout.Token);
        await AvatarWire.SendAsync(resumed, new { type = "ping" }, timeout.Token);
        await Until(resumed, "pong", timeout.Token);
        Assert.Equal(1, ai.Calls); // Reconnecting cannot resubmit a paid turn.
        Assert.Equal(priorCount, session.History.Count);
        await AvatarWire.SendAsync(resumed, new { type = "listen", mode = "hands-free" }, timeout.Token);
        await Until(resumed, "listening", timeout.Token);
        await resumed.SendAsync(new byte[6400], WebSocketMessageType.Binary, true, timeout.Token);
        await Until(resumed, "audio-end", timeout.Token);
        Assert.Equal(2, ai.Calls);
        Assert.Equal(priorCount + 1, ai.LastRequest!.History.Count);
        await AvatarWire.SendAsync(resumed, new { type = "end" }, timeout.Token);
        while ((await resumed.ReceiveAsync(new byte[65536], timeout.Token)).MessageType != WebSocketMessageType.Close) { }
        while (!session.Lifetime.IsCancellationRequested) await Task.Delay(5, timeout.Token);
        Assert.Equal(404, Assert.Throws<AvatarException>(() => sessions.Connect(id, "admin")).Status);
        Assert.Empty(session.History);
        await AssertNoReservations(app);

        // Leaving the page ends the microphone connection, but a later visit
        // with its opaque browser-session reference retains the completed context.
        using var nextStart = await client.PostAsJsonAsync("/api/avatar/sessions", new { memoryId = session.MemoryId });
        nextStart.EnsureSuccessStatusCode();
        using var nextPayload = JsonDocument.Parse(await nextStart.Content.ReadAsStringAsync());
        var nextId = nextPayload.RootElement.GetProperty("sessionId").GetGuid();
        Assert.Equal(priorCount + 1, nextPayload.RootElement.GetProperty("rememberedTurns").GetInt32());
        using var nextVisit = await ws.ConnectAsync(new Uri($"wss://localhost/api/avatar/sessions/{nextId}/voice"), timeout.Token);
        await Until(nextVisit, "connected", timeout.Token);
        Assert.Equal(2, ai.Calls); // Restoring memory cannot initiate a paid reply.
        await AvatarWire.SendAsync(nextVisit, new { type = "listen", mode = "hands-free" }, timeout.Token);
        await Until(nextVisit, "listening", timeout.Token);
        await nextVisit.SendAsync(new byte[6400], WebSocketMessageType.Binary, true, timeout.Token);
        await Until(nextVisit, "audio-end", timeout.Token);
        Assert.Equal(priorCount + 2, ai.LastRequest!.History.Count);
        await AvatarWire.SendAsync(nextVisit, new { type = "end" }, timeout.Token);
    }

    [Fact]
    public async Task HeartbeatsKeepIdleSessionsAliveAndMissingHeartbeatReleasesTheConnection()
    {
        var ai = new Reply(); var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var app = App(ai); var configure = app.Overrides!;
        app.Overrides = services => { configure(services); services.AddSingleton<TimeProvider>(clock); };
        using var client = await app.Client("admin"); await app.Antiforgery(client);
        using var start = await client.PostAsJsonAsync("/api/avatar/sessions", new { });
        var id = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetGuid();
        var sessions = app.Services.GetRequiredService<AvatarSessions>(); var session = sessions.Get(id, "admin");
        var ws = app.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request => { request.Headers["Cookie"] = client.DefaultRequestHeaders.GetValues("Cookie").Single(); request.Headers["Origin"] = "https://localhost"; };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await ws.ConnectAsync(new Uri($"wss://localhost/api/avatar/sessions/{id}/voice"), timeout.Token);
        await Until(socket, "connected", timeout.Token);
        for (var i = 0; i < 12; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(20));
            await AvatarWire.SendAsync(socket, new { type = "ping" }, timeout.Token);
            await Until(socket, "pong", timeout.Token);
        }
        Assert.Equal(1, session.Connected); Assert.Equal(0, ai.Calls);
        clock.Advance(TimeSpan.FromSeconds(36));
        try { while ((await socket.ReceiveAsync(new byte[65536], timeout.Token)).MessageType != WebSocketMessageType.Close) { } }
        catch (WebSocketException) { /* Cancelling Receive aborts a half-open socket. */ }
        while (Volatile.Read(ref session.Connected) != 0) await Task.Delay(5, timeout.Token);
        Assert.False(session.Lifetime.IsCancellationRequested);
        Assert.Same(session, sessions.Get(id, "admin"));
        clock.Advance(TimeSpan.FromSeconds(46)); sessions.Sweep();
        Assert.True(session.Lifetime.IsCancellationRequested);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DroppedSpeechProviderAllowsExplicitRetryWithoutReconnectingBrowser(bool duringRecognition)
    {
        var ai = new Reply(); var speech = new Speech { FailRecognition = duringRecognition, FailSynthesis = !duringRecognition };
        using var app = App(ai); var configure = app.Overrides!;
        app.Overrides = services => { configure(services); services.RemoveAll<IAvatarSpeech>(); services.AddSingleton<IAvatarSpeech>(speech); };
        using var client = await app.Client("admin"); await app.Antiforgery(client);
        using var start = await client.PostAsJsonAsync("/api/avatar/sessions", new { });
        var id = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetGuid();
        var ws = app.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request => { request.Headers["Cookie"] = client.DefaultRequestHeaders.GetValues("Cookie").Single(); request.Headers["Origin"] = "https://localhost"; };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await ws.ConnectAsync(new Uri($"wss://localhost/api/avatar/sessions/{id}/voice"), timeout.Token);
        await Until(socket, "connected", timeout.Token);
        await AvatarWire.SendAsync(socket, new { type = "listen", mode = "hands-free" }, timeout.Token);
        if (!duringRecognition)
        {
            await Until(socket, "listening", timeout.Token);
            await socket.SendAsync(new byte[6400], WebSocketMessageType.Binary, true, timeout.Token);
        }
        var error = await Until(socket, "error", timeout.Token);
        Assert.Equal(502, error.GetProperty("status").GetInt32());
        await Until(socket, "usage", timeout.Token);
        await AssertNoReservations(app);
        Assert.Equal(1, speech.ListenCalls);
        Assert.DoesNotContain(app.Services.GetRequiredService<AvatarSessions>().Get(id, "admin").History, x => x.Role == "assistant");
        speech.FailRecognition = speech.FailSynthesis = false;
        await AvatarWire.SendAsync(socket, new { type = "listen", mode = "hands-free" }, timeout.Token);
        await Until(socket, "listening", timeout.Token);
        await socket.SendAsync(new byte[6400], WebSocketMessageType.Binary, true, timeout.Token);
        await Until(socket, "audio-end", timeout.Token);
        Assert.Equal(2, speech.ListenCalls);
        await AvatarWire.SendAsync(socket, new { type = "end" }, timeout.Token);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OptionalHelpTravelsWithTheReplyButIsNeverSpokenOrRemembered(bool translate, bool suggest)
    {
        var ai = new Reply(); var speech = new Speech();
        using var app = App(ai); var configure = app.Overrides!;
        app.Overrides = services => { configure(services); services.RemoveAll<IAvatarSpeech>(); services.AddSingleton<IAvatarSpeech>(speech); };
        using var client = await app.Client("admin"); await app.Antiforgery(client);
        using var start = await client.PostAsJsonAsync("/api/avatar/sessions", new { });
        var id = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetGuid();
        var ws = app.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request => { request.Headers["Cookie"] = client.DefaultRequestHeaders.GetValues("Cookie").Single(); request.Headers["Origin"] = "https://localhost"; };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await ws.ConnectAsync(new Uri($"wss://localhost/api/avatar/sessions/{id}/voice"), timeout.Token);
        await Until(socket, "connected", timeout.Token);
        // Options can change for the next turn without a new session or lost history.
        foreach (var options in new[] { (translate, suggest), (false, false) })
        {
            await AvatarWire.SendAsync(socket, new { type = "listen", mode = "hands-free", translateReplies = options.Item1, suggestAnswers = options.Item2 }, timeout.Token);
            await Until(socket, "listening", timeout.Token);
            await socket.SendAsync(new byte[6400], WebSocketMessageType.Binary, true, timeout.Token);
            var reply = await Until(socket, "reply", timeout.Token);
            Assert.Equal("Hej! Hur mår du?", reply.GetProperty("text").GetString());
            Assert.Equal(options.Item1 ? "Hi! How are you?" : null, reply.GetProperty("translation").GetString());
            Assert.Equal(options.Item2 ? "Hur har din dag varit?" : null, reply.GetProperty("suggestion").GetString());
            Assert.Equal(options.Item1 && options.Item2 ? "How has your day been?" : null, reply.GetProperty("suggestionTranslation").GetString());
            await Until(socket, "audio-end", timeout.Token);
            await Until(socket, "usage", timeout.Token);
            Assert.Equal("Hej! Hur mår du?", speech.SpokenText);
            await AvatarWire.SendAsync(socket, new { type = "played", turnId = reply.GetProperty("turnId").GetString() }, timeout.Token);
            await Until(socket, "idle", timeout.Token);
            Assert.Equal("avatar.reply", ai.LastUsage!.Operation);
            Assert.Equal(id.ToString(), ai.LastUsage.RelatedEntityId);
            await AssertNoReservations(app);
        }
        var history = app.Services.GetRequiredService<AvatarSessions>().Get(id, "admin").History;
        Assert.Equal(4, history.Count);
        Assert.All(history.Where(turn => turn.Role == "assistant"), turn =>
        {
            Assert.Contains("Hej! Hur", turn.ContentJson);
            Assert.DoesNotContain("How", turn.ContentJson);
            Assert.DoesNotContain("suggestion", turn.ContentJson);
        });
        Assert.Equal(2, ai.Calls);
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
        public bool FailRecognition, FailSynthesis;
        public int ListenCalls;
        public string? SpokenText;
        public async Task<string> ListenAsync(AvatarSession session, string mode, ChannelReader<byte[]> audio, Func<string, Task> partial, Func<Task> ready, CancellationToken ct)
        {
            ListenCalls++;
            if (FailRecognition) throw new WebSocketException("Recognition connection lost");
            await ready();
            await foreach (var chunk in audio.ReadAllAsync(ct))
                if (chunk.Length == 0 || mode == "hands-free") return "Hej!";
            return "Hej!";
        }
        public Task SpeakAsync(AvatarSession session, string text, Func<byte[], Task> audio, CancellationToken ct)
        {
            SpokenText = text;
            if (FailSynthesis) throw new WebSocketException("Synthesis connection lost");
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
        public AiUsageContext? LastUsage;
        public bool Wait;
        public bool Fail;
        public bool Cancelled;
        public int Calls;
        public async Task<AgentTurnResult> RunAgentTurnAsync(AgentRequest request, AiUsageContext context, CancellationToken cancellationToken = default)
        {
            Calls++; LastRequest = request; LastUsage = context; Started.TrySetResult();
            if (Fail) throw new InvalidOperationException("Provider unavailable");
            try { if (Wait) await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            if (request.ResponseSchema is not null)
            {
                using var schema = JsonDocument.Parse(JsonSerializer.Serialize(request.ResponseSchema.Schema));
                var values = new Dictionary<string, string> { ["original"] = "Hej! Hur mår du?", ["english"] = "Hi! How are you?",
                    ["suggestion"] = "Hur har din dag varit?", ["suggestionEnglish"] = "How has your day been?" };
                return new(JsonSerializer.Serialize(schema.RootElement.GetProperty("required").EnumerateArray()
                    .ToDictionary(field => field.GetString()!, field => values[field.GetString()!])), []);
            }
            return new("Hej! Hur mår du?", []);
        }
        public Task<T> GenerateStructuredAsync<T>(string prompt, AiUsageContext context, string? model = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ExtractTextFromImageAsync(byte[] image, string contentType, string prompt, AiUsageContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
