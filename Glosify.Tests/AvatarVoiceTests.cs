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
    [Fact]
    public async Task AcceptedListenSuspendsIdleExpiryWhilePreviousTurnDisposes()
    {
        var reply = new Reply(); var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var app = App(reply); var configure = app.Overrides!;
        app.Overrides = services =>
        {
            configure(services); services.AddSingleton<TimeProvider>(clock);
            services.RemoveAll<IGenerativeAiClient>();
            services.AddScoped<IGenerativeAiClient>(sp => new SlowCleanupReply(
                new ReservingReply(reply, sp.GetRequiredService<ResourceQuotaService>(), sp.GetRequiredService<RequestResourceReservations>()),
                disposing, release.Task));
        };
        using var client = await app.Client("admin"); await app.Antiforgery(client);
        using var start = await client.PostAsJsonAsync("/api/avatar/sessions", new { });
        var id = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetGuid();
        var session = app.Services.GetRequiredService<AvatarSessions>().Get(id, "admin");
        var ws = app.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request => { request.Headers["Cookie"] = client.DefaultRequestHeaders.GetValues("Cookie").Single(); request.Headers["Origin"] = "https://localhost"; };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var socket = await ws.ConnectAsync(new Uri($"wss://localhost/api/avatar/sessions/{id}/voice"), timeout.Token);
        try
        {
            await Until(socket, "connected", timeout.Token);
            await AvatarWire.SendAsync(socket, new { type = "listen", mode = "push-to-talk" }, timeout.Token);
            var listening = await Until(socket, "listening", timeout.Token);
            await AvatarWire.SendAsync(socket, new { type = "commit" }, timeout.Token);
            await Until(socket, "audio-end", timeout.Token); await Until(socket, "usage", timeout.Token);
            await disposing.Task.WaitAsync(timeout.Token);
            await AvatarWire.SendAsync(socket, new { type = "played", turnId = listening.GetProperty("turnId").GetString() }, timeout.Token);
            await Until(socket, "idle", timeout.Token);
            for (var i = 0; i < 5; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(10));
                await AvatarWire.SendAsync(socket, new { type = "ping" }, timeout.Token); await Until(socket, "pong", timeout.Token);
            }
            using var cancellation = reply.LastCancellation.Register(() => accepted.TrySetResult());
            await AvatarWire.SendAsync(socket, new { type = "listen", mode = "push-to-talk" }, timeout.Token);
            await accepted.Task.WaitAsync(timeout.Token); // StopTurn has accepted the new command.
            clock.Advance(TimeSpan.FromSeconds(20)); // Cross the old idle deadline while disposal is blocked.
            release.TrySetResult();
            await Until(socket, "listening", timeout.Token);
            await AvatarWire.SendAsync(socket, new { type = "ping" }, timeout.Token);
            await Until(socket, "pong", timeout.Token);
            Assert.False(session.Lifetime.IsCancellationRequested);
            Assert.Equal(1, reply.Calls);
            await AvatarWire.SendAsync(socket, new { type = "end" }, timeout.Token);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task PreparationHasNoUsageAndIdleExpiryIgnoresHeartbeats(bool afterTurn, bool prepare)
    {
        var ai = new Reply(); var speech = new Speech { PlaybackChunks = afterTurn ? 40 : 1 };
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var app = App(ai); var configure = app.Overrides!;
        app.Overrides = services =>
        {
            configure(services); services.AddSingleton<TimeProvider>(clock);
            services.RemoveAll<IAvatarSpeech>(); services.AddScoped<IAvatarSpeech>(_ => speech);
        };
        using var client = await app.Client("admin"); await app.Antiforgery(client);
        using var start = await client.PostAsJsonAsync("/api/avatar/sessions", new { preconnect = true });
        var id = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetGuid();
        var sessions = app.Services.GetRequiredService<AvatarSessions>(); var session = sessions.Get(id, "admin");
        var ws = app.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request => { request.Headers["Cookie"] = client.DefaultRequestHeaders.GetValues("Cookie").Single(); request.Headers["Origin"] = "https://localhost"; };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var socket = await ws.ConnectAsync(new Uri($"wss://localhost/api/avatar/sessions/{id}/voice"), timeout.Token);
        await Until(socket, "connected", timeout.Token);
        if (prepare) await AvatarWire.SendAsync(socket, new { type = "prepare", mode = "push-to-talk" }, timeout.Token);
        await AvatarWire.SendAsync(socket, new { type = "ping" }, timeout.Token);
        await Until(socket, "pong", timeout.Token);
        Assert.Equal(prepare ? 1 : 0, speech.PrepareCalls); Assert.Equal(0, speech.ListenCalls); Assert.Equal(0, ai.Calls);
        await app.Seed(async db => Assert.Empty(await db.Set<AvatarUsageOperation>().ToListAsync()));
        if (afterTurn)
        {
            await AvatarWire.SendAsync(socket, new { type = "listen", mode = "push-to-talk" }, timeout.Token);
            var listening = await Until(socket, "listening", timeout.Token);
            // An active utterance outlives the preparation lease.
            for (var i = 0; i < 2; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(20));
                await AvatarWire.SendAsync(socket, new { type = "ping" }, timeout.Token);
                await Until(socket, "pong", timeout.Token);
            }
            await socket.SendAsync(new byte[6400], WebSocketMessageType.Binary, true, timeout.Token);
            await AvatarWire.SendAsync(socket, new { type = "commit" }, timeout.Token);
            await Until(socket, "audio-end", timeout.Token);
            await Until(socket, "usage", timeout.Token);
            // Eighty seconds of generated audio must not be cut off by idle cleanup.
            for (var i = 0; i < 7; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(10));
                await AvatarWire.SendAsync(socket, new { type = "ping" }, timeout.Token);
                await Until(socket, "pong", timeout.Token);
            }
            await AvatarWire.SendAsync(socket, new { type = "played", turnId = listening.GetProperty("turnId").GetString() }, timeout.Token);
            await Until(socket, "idle", timeout.Token);
        }
        var seconds = afterTurn ? 60 : 30;
        for (var elapsed = 10; elapsed < seconds; elapsed += 10)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            await AvatarWire.SendAsync(socket, new { type = "ping" }, timeout.Token);
            await Until(socket, "pong", timeout.Token);
        }
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal("idle", (await Until(socket, "ended", timeout.Token)).GetProperty("reason").GetString());
        try { while ((await socket.ReceiveAsync(new byte[65536], timeout.Token)).MessageType != WebSocketMessageType.Close) { } }
        catch (WebSocketException) { /* Cancellation can abort the receive side. */ }
        while (!session.Lifetime.IsCancellationRequested) await Task.Delay(5, timeout.Token);
        Assert.Throws<AvatarException>(() => sessions.Get(id, "admin"));
        Assert.True(speech.Disposed);
        Assert.Equal(afterTurn ? 1 : 0, ai.Calls);
    }

    [Fact]
    public async Task UsedSessionReconnectRetainsSixtySecondIdleLease()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var app = App(new Reply()); var configure = app.Overrides!;
        app.Overrides = services => { configure(services); services.AddSingleton<TimeProvider>(clock); };
        using var client = await app.Client("admin"); await app.Antiforgery(client);
        using var start = await client.PostAsJsonAsync("/api/avatar/sessions", new { });
        var id = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetGuid();
        var session = app.Services.GetRequiredService<AvatarSessions>().Get(id, "admin");
        var ws = app.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request => { request.Headers["Cookie"] = client.DefaultRequestHeaders.GetValues("Cookie").Single(); request.Headers["Origin"] = "https://localhost"; };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using (var first = await ws.ConnectAsync(new Uri($"wss://localhost/api/avatar/sessions/{id}/voice"), timeout.Token))
        {
            await Until(first, "connected", timeout.Token);
            await AvatarWire.SendAsync(first, new { type = "listen", mode = "push-to-talk" }, timeout.Token);
            await Until(first, "listening", timeout.Token);
            await first.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Transport lost", timeout.Token);
            while ((await first.ReceiveAsync(new byte[65536], timeout.Token)).MessageType != WebSocketMessageType.Close) { }
            while (Volatile.Read(ref session.Connected) != 0) await Task.Delay(5, timeout.Token);
        }
        Assert.True(session.HasStartedTurn);
        using var socket = await ws.ConnectAsync(new Uri($"wss://localhost/api/avatar/sessions/{id}/voice"), timeout.Token);
        await Until(socket, "connected", timeout.Token);
        await AvatarWire.SendAsync(socket, new { type = "prepare", mode = "push-to-talk" }, timeout.Token);
        await AvatarWire.SendAsync(socket, new { type = "ping" }, timeout.Token); await Until(socket, "pong", timeout.Token);
        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            await AvatarWire.SendAsync(socket, new { type = "ping" }, timeout.Token); await Until(socket, "pong", timeout.Token);
        }
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal("idle", (await Until(socket, "ended", timeout.Token)).GetProperty("reason").GetString());
        try { while ((await socket.ReceiveAsync(new byte[65536], timeout.Token)).MessageType != WebSocketMessageType.Close) { } }
        catch (WebSocketException) { }
        while (!session.Lifetime.IsCancellationRequested) await Task.Delay(5, timeout.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivationBoundsTheIdleLeaseOncePerTransport(bool prepare)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var app = App(new Reply()); var configure = app.Overrides!;
        app.Overrides = services => { configure(services); services.AddSingleton<TimeProvider>(clock); };
        using var client = await app.Client("admin"); await app.Antiforgery(client);
        using var start = await client.PostAsJsonAsync("/api/avatar/sessions", new { preconnect = prepare });
        var id = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetGuid();
        var session = app.Services.GetRequiredService<AvatarSessions>().Get(id, "admin");
        var ws = app.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request => { request.Headers["Cookie"] = client.DefaultRequestHeaders.GetValues("Cookie").Single(); request.Headers["Origin"] = "https://localhost"; };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var socket = await ws.ConnectAsync(new Uri($"wss://localhost/api/avatar/sessions/{id}/voice"), timeout.Token);
        await Until(socket, "connected", timeout.Token);
        if (prepare) await AvatarWire.SendAsync(socket, new { type = "prepare", mode = "push-to-talk" }, timeout.Token);
        await AvatarWire.SendAsync(socket, new { type = "ping" }, timeout.Token); await Until(socket, "pong", timeout.Token);
        async Task Elapse(int seconds)
        {
            for (var elapsed = 0; elapsed < seconds; elapsed += 10)
            {
                clock.Advance(TimeSpan.FromSeconds(10));
                await AvatarWire.SendAsync(socket, new { type = "ping" }, timeout.Token); await Until(socket, "pong", timeout.Token);
            }
        }
        await Elapse(20);
        // Talk gives either session a 60-second idle lease before its first turn.
        await AvatarWire.SendAsync(socket, new { type = "activate" }, timeout.Token);
        await AvatarWire.SendAsync(socket, new { type = "ping" }, timeout.Token); await Until(socket, "pong", timeout.Token);
        await Elapse(50);
        Assert.False(session.Lifetime.IsCancellationRequested);
        Assert.False(session.HasStartedTurn);
        // Repeated activation cannot keep an unused connection open indefinitely.
        await AvatarWire.SendAsync(socket, new { type = "activate" }, timeout.Token);
        await AvatarWire.SendAsync(socket, new { type = "ping" }, timeout.Token); await Until(socket, "pong", timeout.Token);
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal("idle", (await Until(socket, "ended", timeout.Token)).GetProperty("reason").GetString());
        try { while ((await socket.ReceiveAsync(new byte[65536], timeout.Token)).MessageType != WebSocketMessageType.Close) { } }
        catch (WebSocketException) { }
        while (!session.Lifetime.IsCancellationRequested) await Task.Delay(5, timeout.Token);
    }

    [Theory]
    [InlineData("limit")]
    [InlineData("turn")]
    [InlineData("end")]
    public async Task PreparedRecognitionConnectionsAreBoundedAndReleased(string scenario)
    {
        var speech = new Speech();
        using var app = App(new Reply()); var configure = app.Overrides!;
        app.Overrides = services =>
        {
            configure(services);
            services.RemoveAll<IAvatarSpeech>(); services.AddScoped<IAvatarSpeech>(_ => speech);
            services.Configure<AvatarOptions>(options => options.MaxPreparedRecognitions = 1);
        };
        using var client = await app.Client("admin"); await app.Antiforgery(client);
        var sessions = app.Services.GetRequiredService<AvatarSessions>();
        // Another conversation already holds the only prepared connection.
        if (scenario == "limit") Assert.True(sessions.TryReservePreparation(1));
        using var start = await client.PostAsJsonAsync("/api/avatar/sessions", new { preconnect = true });
        var id = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetGuid();
        var ws = app.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request => { request.Headers["Cookie"] = client.DefaultRequestHeaders.GetValues("Cookie").Single(); request.Headers["Origin"] = "https://localhost"; };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await ws.ConnectAsync(new Uri($"wss://localhost/api/avatar/sessions/{id}/voice"), timeout.Token);
        await Until(socket, "connected", timeout.Token);
        await AvatarWire.SendAsync(socket, new { type = "prepare", mode = "push-to-talk" }, timeout.Token);
        await AvatarWire.SendAsync(socket, new { type = "ping" }, timeout.Token); await Until(socket, "pong", timeout.Token);
        Assert.Equal(scenario == "limit" ? 0 : 1, speech.PrepareCalls);
        Assert.False(sessions.TryReservePreparation(1));
        if (scenario == "end")
        {
            await AvatarWire.SendAsync(socket, new { type = "end" }, timeout.Token);
            while ((await socket.ReceiveAsync(new byte[65536], timeout.Token)).MessageType != WebSocketMessageType.Close) { }
            while (!sessions.TryReservePreparation(1)) await Task.Delay(5, timeout.Token);
            return;
        }
        if (scenario == "limit") sessions.ReleasePreparation();
        // Over the limit the first turn connects normally; otherwise it takes over the slot.
        await AvatarWire.SendAsync(socket, new { type = "listen", mode = "push-to-talk" }, timeout.Token);
        await Until(socket, "listening", timeout.Token);
        Assert.Equal(1, speech.ListenCalls);
        Assert.True(sessions.TryReservePreparation(1));
        sessions.ReleasePreparation();
        await AvatarWire.SendAsync(socket, new { type = "end" }, timeout.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedTurnAdmissionKeepsPreparationCapacityUntilRetryOrDisposal(bool retry)
    {
        var admission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var speech = new Speech { AdmissionGate = admission, RejectAdmission = true };
        using var app = App(new Reply()); var configure = app.Overrides!;
        app.Overrides = services =>
        {
            configure(services);
            services.RemoveAll<IAvatarSpeech>(); services.AddScoped<IAvatarSpeech>(_ => speech);
            services.Configure<AvatarOptions>(options => options.MaxPreparedRecognitions = 1);
        };
        using var client = await app.Client("admin"); await app.Antiforgery(client);
        var sessions = app.Services.GetRequiredService<AvatarSessions>();
        using var start = await client.PostAsJsonAsync("/api/avatar/sessions", new { preconnect = true });
        var id = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetGuid();
        var ws = app.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request => { request.Headers["Cookie"] = client.DefaultRequestHeaders.GetValues("Cookie").Single(); request.Headers["Origin"] = "https://localhost"; };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await ws.ConnectAsync(new Uri($"wss://localhost/api/avatar/sessions/{id}/voice"), timeout.Token);
        await Until(socket, "connected", timeout.Token);
        await AvatarWire.SendAsync(socket, new { type = "prepare", mode = "push-to-talk" }, timeout.Token);
        await AvatarWire.SendAsync(socket, new { type = "listen", mode = "push-to-talk" }, timeout.Token);
        await Until(socket, "connecting", timeout.Token);
        Assert.Equal(1, speech.PrepareCalls);
        Assert.False(sessions.TryReservePreparation(1));
        admission.SetResult();
        await Until(socket, "error", timeout.Token);
        await Until(socket, "usage", timeout.Token);
        Assert.False(sessions.TryReservePreparation(1));
        Assert.False(speech.Disposed);
        if (retry)
        {
            speech.RejectAdmission = false;
            await AvatarWire.SendAsync(socket, new { type = "listen", mode = "push-to-talk" }, timeout.Token);
            await Until(socket, "listening", timeout.Token);
            Assert.Equal(2, speech.ListenCalls);
            Assert.True(sessions.TryReservePreparation(1));
            Assert.False(sessions.TryReservePreparation(1));
            sessions.ReleasePreparation();
        }
        await AvatarWire.SendAsync(socket, new { type = "end" }, timeout.Token);
        while ((await socket.ReceiveAsync(new byte[65536], timeout.Token)).MessageType != WebSocketMessageType.Close) { }
        while (!speech.Disposed) await Task.Delay(5, timeout.Token);
        Assert.True(sessions.TryReservePreparation(1));
        Assert.False(sessions.TryReservePreparation(1));
        sessions.ReleasePreparation();
    }

    [Fact]
    public async Task InterruptDuringSlowPreparationDoesNotWaitForTheProvider()
    {
        var speech = new Speech { PrepareGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var app = App(new Reply()); var configure = app.Overrides!;
        app.Overrides = services =>
        {
            configure(services);
            services.RemoveAll<IAvatarSpeech>(); services.AddScoped<IAvatarSpeech>(_ => speech);
        };
        using var client = await app.Client("admin"); await app.Antiforgery(client);
        using var start = await client.PostAsJsonAsync("/api/avatar/sessions", new { preconnect = true });
        var id = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetGuid();
        var ws = app.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request => { request.Headers["Cookie"] = client.DefaultRequestHeaders.GetValues("Cookie").Single(); request.Headers["Origin"] = "https://localhost"; };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await ws.ConnectAsync(new Uri($"wss://localhost/api/avatar/sessions/{id}/voice"), timeout.Token);
        try
        {
            await Until(socket, "connected", timeout.Token);
            await AvatarWire.SendAsync(socket, new { type = "prepare", mode = "push-to-talk" }, timeout.Token);
            await AvatarWire.SendAsync(socket, new { type = "listen", mode = "push-to-talk" }, timeout.Token);
            await AvatarWire.SendAsync(socket, new { type = "interrupt" }, timeout.Token);
            // The command loop answers while the provider connect is still pending.
            await Until(socket, "idle", timeout.Token);
            await AvatarWire.SendAsync(socket, new { type = "ping" }, timeout.Token); await Until(socket, "pong", timeout.Token);
            Assert.False(speech.PrepareGate.Task.IsCompleted);
            Assert.Equal(0, speech.ListenCalls);
            // A later turn still uses the completed preparation.
            speech.PrepareGate.TrySetResult();
            await AvatarWire.SendAsync(socket, new { type = "listen", mode = "push-to-talk" }, timeout.Token);
            await Until(socket, "listening", timeout.Token);
            Assert.Equal(1, speech.PrepareCalls);
            await AvatarWire.SendAsync(socket, new { type = "end" }, timeout.Token);
        }
        finally { speech.PrepareGate.TrySetResult(); }
    }

    [Theory]
    [InlineData("push-to-talk", "rain", "Rain")]
    [InlineData("hands-free", "rowan", "Rowan")]
    public async Task AudioProducesCaptionsAndReplyAndPlaybackAcknowledgementCommitsMemory(string mode, string characterId, string name)
    {
        var ai = new Reply();
        var speech = new Speech { PrepareGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var app = App(ai);
        var configure = app.Overrides!;
        app.Overrides = services =>
        {
            configure(services);
            services.RemoveAll<IAvatarSpeech>(); services.AddScoped<IAvatarSpeech>(_ => speech);
        };
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
        // Foreground starts bypass optional preparation, even if it would stall.
        await AvatarWire.SendAsync(socket, new { type = "activate" }, timeout.Token);
        await AvatarWire.SendAsync(socket, new { type = "listen", mode }, timeout.Token);
        var listening = await Until(socket, "listening", timeout.Token);
        Assert.Equal(0, speech.PrepareCalls);
        Assert.False(speech.PrepareGate.Task.IsCompleted);
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
        var sawIdle = false;
        var sawSavedStatus = false;
        while (!sawIdle || !sawSavedStatus)
        {
            using var frame = await AvatarWire.ReadAsync(socket, timeout.Token, 3 * 1024 * 1024);
            if (frame.RootElement.GetProperty("type").GetString() == "idle") sawIdle = true;
            if (frame.RootElement.GetProperty("type").GetString() == "transcript-status"
                && frame.RootElement.GetProperty("status").GetProperty("turnCount").GetInt32() == 2)
            {
                Assert.Equal(session.MemoryId, frame.RootElement.GetProperty("status").GetProperty("conversationId").GetGuid());
                Assert.True(frame.RootElement.GetProperty("status").GetProperty("revision").GetInt64() > 0);
                sawSavedStatus = true;
            }
        }
        Assert.Equal(2, session.History.Count);
        Assert.Equal(new[] { "user", "assistant" }, app.Services.GetRequiredService<AvatarSessions>()
            .Snapshot(session.MemoryId, "admin", "sv").Turns.Select(t => t.Role));
        Assert.Contains("Swedish", ai.LastRequest!.SystemInstruction);
        Assert.Contains("You are " + name + ",", ai.LastRequest.SystemInstruction);
        Assert.Empty(ai.LastRequest.Tools);
        await AvatarWire.SendAsync(socket, new { type = "end" }, timeout.Token);
        await socket.ReceiveAsync(new byte[4096], timeout.Token);
    }

    [Fact]
    public async Task CoalescedTranscriptSavesDoNotDelayVoiceAndDrainLatestTurnsOnEnd()
    {
        var ai = new Reply();
        using var app = App(ai);
        using var client = await app.Client("admin"); await app.Antiforgery(client);
        (await client.PostAsJsonAsync("/api/avatar/preferences", new { autoSaveConversations = true })).EnsureSuccessStatusCode();
        using var start = await client.PostAsJsonAsync("/api/avatar/sessions", new { language = "sv" });
        var id = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetGuid();
        var sessions = app.Services.GetRequiredService<AvatarSessions>();
        using var heldWrite = await sessions.LockTranscriptWritesAsync("admin", default);
        var ws = app.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request =>
        {
            request.Headers["Cookie"] = client.DefaultRequestHeaders.GetValues("Cookie").Single();
            request.Headers["Origin"] = "https://localhost";
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var socket = await ws.ConnectAsync(new Uri($"wss://localhost/api/avatar/sessions/{id}/voice"), timeout.Token);
        await Until(socket, "connected", timeout.Token);
        for (var i = 0; i < 3; i++)
        {
            await AvatarWire.SendAsync(socket, new { type = "listen", mode = "hands-free" }, timeout.Token);
            var listening = await Until(socket, "listening", timeout.Token);
            await socket.SendAsync(new byte[6400], WebSocketMessageType.Binary, true, timeout.Token);
            var reply = await Until(socket, "reply", timeout.Token);
            Assert.Equal("Hej! Hur mår du?", reply.GetProperty("text").GetString());
            await Until(socket, "audio-end", timeout.Token);
            await AvatarWire.SendAsync(socket, new { type = "played", turnId = listening.GetProperty("turnId").GetString() }, timeout.Token);
            await Until(socket, "idle", timeout.Token);
        }
        // Six notifications while saves are blocked exceed the one-slot queue.
        // The last turns are still present when its coalesced update snapshots.
        await app.Seed(async db => Assert.Empty(await db.RealtimeTranslationTranscriptSegments.ToListAsync()));
        var memoryId = sessions.Get(id, "admin").MemoryId;
        await AvatarWire.SendAsync(socket, new { type = "end" }, timeout.Token);
        heldWrite.Dispose();
        while ((await socket.ReceiveAsync(new byte[65536], timeout.Token)).MessageType != WebSocketMessageType.Close) { }
        await app.Seed(async db =>
        {
            var turns = await db.RealtimeTranslationTranscriptSegments.OrderBy(s => s.Sequence).ToListAsync();
            Assert.Equal(6, turns.Count);
            Assert.Equal(Enumerable.Range(1, 6), turns.Select(t => t.Sequence));
            Assert.Equal(new[] { "You", "Rain", "You", "Rain", "You", "Rain" }, turns.Select(t => t.Speaker));
        });
        Assert.Equal(6, sessions.CachedTranscriptStatus(memoryId, "admin", "sv").SavedTurns);
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
        var interruptedSession = app.Services.GetRequiredService<AvatarSessions>().Get(id, "admin");
        Assert.Equal("user", Assert.Single(app.Services.GetRequiredService<AvatarSessions>()
            .Snapshot(interruptedSession.MemoryId, "admin", "sv").Turns).Role);
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
            // The independent status delivery has its own ten-second send budget.
            // Finish it before advancing the fake clock to test the AI deadline.
            await Until(socket, "transcript-status", timeout.Token);
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
    public async Task LearnerConnectionPassesIdentityChecksUntilLockedOut()
    {
        var clock = new IdentityCheckClock(DateTimeOffset.UtcNow);
        using var app = App(new Reply()); var configure = app.Overrides!;
        app.Overrides = services => { configure(services); services.AddSingleton<TimeProvider>(clock); };
        using var client = await app.Client("learner"); await app.Antiforgery(client);
        using var start = await client.PostAsJsonAsync("/api/avatar/sessions", new { });
        var id = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetGuid();
        var ws = app.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request => { request.Headers["Cookie"] = client.DefaultRequestHeaders.GetValues("Cookie").Single(); request.Headers["Origin"] = "https://localhost"; };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await ws.ConnectAsync(new Uri($"wss://localhost/api/avatar/sessions/{id}/voice"), timeout.Token);
        await Until(socket, "connected", timeout.Token);
        await clock.IdentityChecks.Reader.ReadAsync(timeout.Token);
        clock.Advance(TimeSpan.FromSeconds(30));
        // The monitor only waits for another check after this one accepted the learner.
        await clock.IdentityChecks.Reader.ReadAsync(timeout.Token);
        await AvatarWire.SendAsync(socket, new { type = "ping" }, timeout.Token);
        await Until(socket, "pong", timeout.Token);

        await app.Seed(async db =>
        {
            var learner = await db.Users.SingleAsync(x => x.Id == "learner");
            learner.LockoutEnabled = true; learner.LockoutEnd = DateTimeOffset.MaxValue;
            await db.SaveChangesAsync();
        });
        clock.Advance(TimeSpan.FromSeconds(30));
        try { while ((await socket.ReceiveAsync(new byte[65536], timeout.Token)).MessageType != WebSocketMessageType.Close) { } }
        catch (WebSocketException) { } // Cancelling the pending server receive may abort rather than close.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullLivePoolPausesAnIdleConversationWithANoticeForAnotherLearner(bool emptyListen)
    {
        using var app = App(new Reply());
        using var idle = await app.Client("learner"); await app.Antiforgery(idle);
        using var start = await idle.PostAsJsonAsync("/api/avatar/sessions", new { });
        var id = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetGuid();
        var ws = app.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request => { request.Headers["Cookie"] = idle.DefaultRequestHeaders.GetValues("Cookie").Single(); request.Headers["Origin"] = "https://localhost"; };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await ws.ConnectAsync(new Uri($"wss://localhost/api/avatar/sessions/{id}/voice"), timeout.Token);
        await Until(socket, "connected", timeout.Token);
        if (emptyListen)
        {
            await AvatarWire.SendAsync(socket, new { type = "listen", mode = "push-to-talk" }, timeout.Token);
            await Until(socket, "listening", timeout.Token);
        }
        // Every other slot belongs to a learner who is speaking.
        var sessions = app.Services.GetRequiredService<AvatarSessions>();
        for (var i = 1; i < AvatarSessions.MaximumLiveSessions; i++)
            sessions.Create($"speaker-{i}", Glosify.Services.Language.QuizLanguageCatalog.Find("sv")!, "free")
                .KeepActiveUntil(DateTimeOffset.UtcNow.AddMinutes(5));

        using var newcomer = await app.Client("admin"); await app.Antiforgery(newcomer);
        using var admitted = await newcomer.PostAsJsonAsync("/api/avatar/sessions", new { });
        admitted.EnsureSuccessStatusCode();
        Assert.Equal("busy", (await Until(socket, "ended", timeout.Token)).GetProperty("reason").GetString());
        try { while ((await socket.ReceiveAsync(new byte[65536], timeout.Token)).MessageType != WebSocketMessageType.Close) { } }
        catch (WebSocketException) { /* Cancellation can abort the receive side. */ }
    }

    [Theory]
    [InlineData("generation", 0, false)]
    [InlineData("synthesis", 0, false)]
    [InlineData("recognition", 0, false)]
    [InlineData("generation", 63, false)]
    [InlineData("synthesis", 63, false)]
    [InlineData("recognition", 21, false)]
    [InlineData("generation", 63, true)]
    [InlineData("synthesis", 63, true)]
    [InlineData("recognition", 21, true)]
    public async Task FullLivePoolProtectsPendingProviderWorkThenUsesTheCompletionLease(string stage, int delay, bool expire)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ai = new Reply { Gate = stage == "generation" ? gate : null };
        var speech = new Speech { SynthesisGate = stage == "synthesis" ? gate : null,
            RecognitionGate = stage == "recognition" ? gate : null };
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var app = App(ai); var configure = app.Overrides!;
        app.Overrides = services =>
        {
            configure(services); services.AddSingleton<TimeProvider>(clock);
            services.RemoveAll<IAvatarSpeech>(); services.AddSingleton<IAvatarSpeech>(speech);
        };
        using var client = await app.Client("admin"); await app.Antiforgery(client);
        using var start = await client.PostAsJsonAsync("/api/avatar/sessions", new { });
        var id = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetGuid();
        var ws = app.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request => { request.Headers["Cookie"] = client.DefaultRequestHeaders.GetValues("Cookie").Single(); request.Headers["Origin"] = "https://localhost"; };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var socket = await ws.ConnectAsync(new Uri($"wss://localhost/api/avatar/sessions/{id}/voice"), timeout.Token);
        await Until(socket, "connected", timeout.Token);
        var sessions = app.Services.GetRequiredService<AvatarSessions>();
        var active = sessions.Get(id, "admin");
        for (var i = 1; i < AvatarSessions.MaximumLiveSessions; i++)
        {
            var other = sessions.Create($"speaker-{i}", active.Language, "free");
            sessions.Connect(other.Id, other.UserId);
            other.KeepActiveUntil(clock.GetUtcNow().AddMinutes(5));
        }
        await AvatarWire.SendAsync(socket, new { type = "listen", mode = "push-to-talk" }, timeout.Token);
        await Until(socket, "listening", timeout.Token);
        await socket.SendAsync(new byte[6400], WebSocketMessageType.Binary, true, timeout.Token);
        await AvatarWire.SendAsync(socket, new { type = "commit" }, timeout.Token);
        await (stage == "recognition" ? speech.RecognitionCommitted.Task :
            stage == "synthesis" ? speech.SynthesisStarted.Task : ai.Started.Task).WaitAsync(timeout.Token);
        if (stage != "recognition") await Until(socket, "transcript-status", timeout.Token);
        for (var elapsed = 0; elapsed < delay; elapsed += 21)
        {
            clock.Advance(TimeSpan.FromSeconds(21));
            await AvatarWire.SendAsync(socket, new { type = "ping" }, timeout.Token);
            await Until(socket, "pong", timeout.Token);
        }
        Assert.Equal(429, Assert.Throws<AvatarException>(() => sessions.Create("newcomer", active.Language, "free")).Status);
        Assert.False(active.Preemption.IsCancellationRequested);
        Assert.False(ai.LastCancellation.IsCancellationRequested);

        if (expire)
        {
            // Keep the transport alive while crossing the original 120-second turn deadline.
            var elapsed = delay;
            while (elapsed + 20 < 120)
            {
                clock.Advance(TimeSpan.FromSeconds(20));
                elapsed += 20;
                await AvatarWire.SendAsync(socket, new { type = "ping" }, timeout.Token);
                await Until(socket, "pong", timeout.Token);
            }
            clock.Advance(TimeSpan.FromSeconds(121 - elapsed));
            Assert.Equal(504, (await Until(socket, "error", timeout.Token)).GetProperty("status").GetInt32());
        }
        else
        {
            gate.SetResult();
            await Until(socket, "audio-end", timeout.Token);
        }
        await Until(socket, "usage", timeout.Token);
        // Early completion releases the unused portion of the provider deadline;
        // slow completion starts a fresh idle/playback lease.
        Assert.Equal(clock.GetUtcNow().AddSeconds(expire ? 60 : 62), active.KeptUntil);
        Assert.Equal(429, Assert.Throws<AvatarException>(() => sessions.Create("newcomer", active.Language, "free")).Status);
        await AvatarWire.SendAsync(socket, new { type = "end" }, timeout.Token);
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
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidListenCannotReclaimOtherLearnersTranscripts(bool invalidMode)
    {
        using var app = App(new Reply());
        using var client = await app.Client("admin"); await app.Antiforgery(client);
        using var start = await client.PostAsJsonAsync("/api/avatar/sessions", new { });
        var id = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetGuid();
        var sessions = app.Services.GetRequiredService<AvatarSessions>(); var session = sessions.Get(id, "admin");
        var retained = new List<AvatarSession>();
        for (var i = 0; i < 16; i++)
        {
            var archive = sessions.Create($"owner-{i}", session.Language, "free");
            for (var turn = 0; turn < 40; turn++)
                sessions.RecordTurn(archive, turn.ToString(), "user", new string('x', 12_000));
            sessions.Remove(archive); retained.Add(archive);
        }
        var ws = app.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request => { request.Headers["Cookie"] = client.DefaultRequestHeaders.GetValues("Cookie").Single(); request.Headers["Origin"] = "https://localhost"; };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await ws.ConnectAsync(new Uri($"wss://localhost/api/avatar/sessions/{id}/voice"), timeout.Token);
        await Until(socket, "connected", timeout.Token);
        await AvatarWire.SendAsync(socket, new { type = "listen", mode = invalidMode ? "invalid" : "hands-free",
            translateReplies = invalidMode ? (object)false : "invalid" }, timeout.Token);
        Assert.Equal(400, (await Until(socket, "error", timeout.Token)).GetProperty("status").GetInt32());
        Assert.All(retained, s => Assert.Equal(40, sessions.Snapshot(s.MemoryId, s.UserId, s.Language.Code).Turns.Length));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreemptionDuringDisconnectCleanupDoesNotOfferADeadReconnect(bool preempt)
    {
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var app = App(new Reply()); var configure = app.Overrides!;
        app.Overrides = services =>
        {
            configure(services);
            services.RemoveAll<IAvatarSpeech>();
            services.AddScoped<IAvatarSpeech>(_ => new SlowCleanupSpeech(new Speech(), disposing, release.Task));
        };
        using var client = await app.Client("admin"); await app.Antiforgery(client);
        using var start = await client.PostAsJsonAsync("/api/avatar/sessions", new { });
        var id = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetGuid();
        var sessions = app.Services.GetRequiredService<AvatarSessions>(); var session = sessions.Get(id, "admin");
        var ws = app.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request => { request.Headers["Cookie"] = client.DefaultRequestHeaders.GetValues("Cookie").Single(); request.Headers["Origin"] = "https://localhost"; };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var socket = await ws.ConnectAsync(new Uri($"wss://localhost/api/avatar/sessions/{id}/voice"), timeout.Token);
        try
        {
            await Until(socket, "connected", timeout.Token);
            sessions.RecordTurn(session, "1", "user", "Keep this conversation");
            await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "Network lost", timeout.Token);
            await disposing.Task.WaitAsync(timeout.Token); // Lifetime and idle monitor have already stopped.
            if (preempt)
            {
                for (var i = 1; i < AvatarSessions.MaximumLiveSessions; i++)
                {
                    var other = sessions.Create($"speaker-{i}", session.Language, "free");
                    sessions.Connect(other.Id, other.UserId);
                    other.KeepActiveUntil(DateTimeOffset.UtcNow.AddMinutes(5));
                }
                sessions.Create("newcomer", session.Language, "free");
                Assert.True(session.Preemption.IsCancellationRequested);
            }
            release.TrySetResult();
            WebSocketReceiveResult closed;
            do { closed = await socket.ReceiveAsync(new byte[65536], timeout.Token); }
            while (closed.MessageType != WebSocketMessageType.Close);
            Assert.Equal(preempt ? WebSocketCloseStatus.NormalClosure : WebSocketCloseStatus.EndpointUnavailable, closed.CloseStatus);
            if (preempt)
            {
                while (!session.Lifetime.IsCancellationRequested) await Task.Delay(5, timeout.Token);
                Assert.Equal(404, Assert.Throws<AvatarException>(() => sessions.Get(id, "admin")).Status);
            }
            else
            {
                while (Volatile.Read(ref session.Connected) != 0) await Task.Delay(5, timeout.Token);
                Assert.Same(session, sessions.Get(id, "admin"));
            }
            Assert.Equal("Keep this conversation", Assert.Single(sessions.Snapshot(session.MemoryId, "admin", session.Language.Code).Turns).Text);
        }
        finally { release.TrySetResult(); }
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
        { if (level >= LogLevel.Warning) Warnings.Enqueue(formatter(state, exception) + (exception is null ? "" : $"\n{exception}")); }
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
    private sealed class Speech : IAvatarSpeech, IDisposable
    {
        public bool FailRecognition, FailSynthesis, RejectAdmission;
        public int ListenCalls, PrepareCalls;
        public int PlaybackChunks = 1;
        public bool Disposed;
        public TaskCompletionSource? PrepareGate, AdmissionGate;
        public TaskCompletionSource? SynthesisGate;
        public TaskCompletionSource SynthesisStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? RecognitionGate;
        public TaskCompletionSource RecognitionCommitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose() => Disposed = true;
        public async Task PrepareAsync(AvatarSession session, string mode, CancellationToken ct)
        {
            PrepareCalls++;
            if (PrepareGate is not null) await PrepareGate.Task.WaitAsync(ct);
        }
        public string? SpokenText;
        public async Task<string> ListenAsync(AvatarSession session, string mode, ChannelReader<byte[]> audio, Func<string, Task> partial, Func<Task> ready, CancellationToken ct)
        {
            ListenCalls++;
            if (AdmissionGate is not null) await AdmissionGate.Task.WaitAsync(ct);
            if (RejectAdmission) throw new InsufficientAiCreditsException(0, 1);
            if (FailRecognition) throw new WebSocketException("Recognition connection lost");
            await ready();
            await foreach (var chunk in audio.ReadAllAsync(ct))
                if (chunk.Length == 0 || mode == "hands-free") break;
            RecognitionCommitted.TrySetResult();
            if (RecognitionGate is not null) await RecognitionGate.Task.WaitAsync(ct);
            return "Hej!";
        }
        public async Task SpeakAsync(AvatarSession session, string text, Func<byte[], Task> audio, CancellationToken ct)
        {
            SpokenText = text;
            SynthesisStarted.TrySetResult();
            if (SynthesisGate is not null) await SynthesisGate.Task.WaitAsync(ct);
            if (FailSynthesis) throw new WebSocketException("Synthesis connection lost");
            var bytes = new byte[96000];
            for (var i = 0; i < bytes.Length / 2; i++)
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2),
                    (short)(Math.Sin(i / 24000d * Math.PI * 2 * 170) * 6500 * (.5 + .5 * Math.Sin(i / 24000d * 18))));
            for (var chunk = 0; chunk < PlaybackChunks; chunk++) await audio(bytes);
        }
    }
    private sealed class IdentityCheckClock(DateTimeOffset start) : FakeTimeProvider(start)
    {
        public Channel<bool> IdentityChecks { get; } = Channel.CreateUnbounded<bool>();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            // The identity monitor is the connection's only 30-second one-shot delay.
            if (dueTime == TimeSpan.FromSeconds(30) && period == Timeout.InfiniteTimeSpan) IdentityChecks.Writer.TryWrite(true);
            return timer;
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

    private sealed class SlowCleanupSpeech(IAvatarSpeech inner, TaskCompletionSource disposing, Task release) : IAvatarSpeech, IAsyncDisposable
    {
        public Task PrepareAsync(AvatarSession session, string mode, CancellationToken ct) => inner.PrepareAsync(session, mode, ct);
        public Task<string> ListenAsync(AvatarSession session, string mode, ChannelReader<byte[]> audio,
            Func<string, Task> partial, Func<Task> ready, CancellationToken ct) => inner.ListenAsync(session, mode, audio, partial, ready, ct);
        public Task SpeakAsync(AvatarSession session, string text, Func<byte[], Task> audio, CancellationToken ct) =>
            inner.SpeakAsync(session, text, audio, ct);
        public async ValueTask DisposeAsync() { disposing.TrySetResult(); await release; }
    }

    private sealed class SlowCleanupReply(IGenerativeAiClient inner, TaskCompletionSource disposing, Task release) : IGenerativeAiClient, IAsyncDisposable
    {
        public Task<AgentTurnResult> RunAgentTurnAsync(AgentRequest request, AiUsageContext context, CancellationToken cancellationToken = default) =>
            inner.RunAgentTurnAsync(request, context, cancellationToken);
        public async ValueTask DisposeAsync() { disposing.TrySetResult(); await release; }
        public Task<T> GenerateStructuredAsync<T>(string prompt, AiUsageContext context, string? model = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ExtractTextFromImageAsync(byte[] image, string contentType, string prompt, AiUsageContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Reply : IGenerativeAiClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AgentRequest? LastRequest;
        public AiUsageContext? LastUsage;
        public CancellationToken LastCancellation;
        public bool Wait;
        public TaskCompletionSource? Gate;
        public bool Fail;
        public bool Cancelled;
        public int Calls;
        public async Task<AgentTurnResult> RunAgentTurnAsync(AgentRequest request, AiUsageContext context, CancellationToken cancellationToken = default)
        {
            Calls++; LastRequest = request; LastUsage = context; LastCancellation = cancellationToken; Started.TrySetResult();
            if (Fail) throw new InvalidOperationException("Provider unavailable");
            try
            {
                if (Wait) await Task.Delay(Timeout.Infinite, cancellationToken);
                if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);
            }
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
