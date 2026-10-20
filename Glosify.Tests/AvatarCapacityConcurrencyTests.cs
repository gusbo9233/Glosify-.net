using System.Net.WebSockets;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Avatar;
using Glosify.Services.Language;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Glosify.Tests;

public sealed class AvatarCapacityConcurrencyTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("bytes")]
    [InlineData("count")]
    public async Task SavingArchiveSurvivesCapacityPressure(string pressure)
    {
        var clock = new FakeTimeProvider();
        var sessions = new AvatarSessions(clock);
        var language = QuizLanguageCatalog.Find("sv")!;
        var target = sessions.Create("target", language, "free");
        var expectedTurns = pressure == "bytes" ? 40 : 1;
        for (var i = 0; i < expectedTurns; i++)
            sessions.RecordTurn(target, i.ToString(), "user", new string('t', 12_000));
        sessions.Remove(target);
        var fillers = pressure == "bytes" ? 15 : pressure == "count" ? 127 : 0;
        for (var i = 0; i < fillers; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            var filler = sessions.Create($"filler-{i}", language, "free");
            for (var j = 0; j < expectedTurns; j++)
                sessions.RecordTurn(filler, j.ToString(), "user", new string('x', 12_000));
            sessions.Remove(filler);
        }
        Assert.Equal(expectedTurns, sessions.Snapshot(target.MemoryId, "target", "sv").Turns.Length);
        var pause = new PausedSave();
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).AddInterceptors(pause).Options;
        await using var db = new GlosifyContext(options) { AccountingBypass = true };
        db.Users.Add(new ApplicationUser { Id = "target", UserName = "target" });
        await db.SaveChangesAsync();
        pause.Armed = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var save = new AvatarTranscriptService(db, sessions, clock)
            .SaveAsync(target.MemoryId, "target", "sv", false, timeout.Token);
        try
        {
            await pause.Entered.Task.WaitAsync(timeout.Token);
            Assert.False(save.IsCompleted);
            if (pressure != "none")
            {
                var newcomer = sessions.Create("newcomer", language, "free");
                if (pressure == "bytes") sessions.CheckTranscriptCapacity(newcomer);
                Assert.Equal(expectedTurns, sessions.Snapshot(target.MemoryId, "target", "sv").Turns.Length);
            }
            pause.Release.TrySetResult();
            var status = await save;
            Assert.Equal(expectedTurns, status.SavedTurns);
            Assert.NotNull(status.TranscriptId);
            await using var verification = new GlosifyContext(options);
            var transcript = await verification.RealtimeTranslationTranscripts.AsNoTracking().SingleAsync(timeout.Token);
            Assert.Equal(target.MemoryId, transcript.AvatarConversationId);
            Assert.Equal("target", transcript.UserId);
            var segments = await verification.RealtimeTranslationTranscriptSegments.AsNoTracking()
                .Where(s => s.TranscriptId == transcript.Id).ToListAsync(timeout.Token);
            Assert.Equal(expectedTurns, segments.Count);
            Assert.All(segments, s => Assert.Equal(new string('t', 12_000), s.Text));
            if (pressure == "count")
            {
                sessions.Create("after-save", language, "free");
                Assert.Equal(404, Assert.Throws<AvatarException>(() => sessions.Snapshot(target.MemoryId, "target", "sv")).Status);
            }
        }
        finally { pause.Release.TrySetResult(); }
    }

    [Fact]
    public async Task HttpSaveSucceedsWhenAdmissionReclaimsAnotherArchive()
    {
        var pause = new PausedSave();
        using var app = new AvatarFixture
        {
            Overrides = services => services.AddDbContext<GlosifyContext>(o => o.AddInterceptors(pause))
        };
        using var client = await app.Client("owner");
        await app.Antiforgery(client);
        var sessions = app.Services.GetRequiredService<AvatarSessions>();
        var language = QuizLanguageCatalog.Find("sv")!;
        var target = sessions.Create("owner", language, "free");
        sessions.RecordTurn(target, "1", "user", "Persist this exact transcript despite eviction.");
        sessions.Remove(target);
        for (var i = 0; i < 127; i++)
        {
            var filler = sessions.Create($"filler-{i}", language, "free");
            sessions.RecordTurn(filler, "1", "user", "Another archive");
            sessions.Remove(filler);
        }
        pause.Armed = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var request = client.PostAsJsonAsync($"/api/avatar/conversations/{target.MemoryId}/transcript", new { }, timeout.Token);
        try
        {
            await pause.Entered.Task.WaitAsync(timeout.Token);
            sessions.Create("newcomer", language, "free");
            Assert.Equal("Persist this exact transcript despite eviction.", Assert.Single(sessions.Snapshot(target.MemoryId, "owner", "sv").Turns).Text);
            pause.Release.TrySetResult();
            using var response = await request;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            Assert.Equal(1, body.RootElement.GetProperty("savedTurns").GetInt32());
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GlosifyContext>();
            var saved = await db.RealtimeTranslationTranscripts.AsNoTracking().SingleAsync(t => t.AvatarConversationId == target.MemoryId, timeout.Token);
            var segment = await db.RealtimeTranslationTranscriptSegments.AsNoTracking().SingleAsync(s => s.TranscriptId == saved.Id, timeout.Token);
            Assert.Equal("Persist this exact transcript despite eviction.", segment.Text);
        }
        finally { pause.Release.TrySetResult(); }
    }

    [Fact]
    public async Task ClosingConnectionsHaveABoundedBacklogUntilCleanupCompletes()
    {
        var tracker = new CleanupTracker();
        using var app = new AvatarFixture
        {
            Overrides = services =>
            {
                services.RemoveAll<IAvatarSpeech>();
                services.AddScoped<IAvatarSpeech>(_ => new PausedCleanupSpeech(tracker));
            }
        };
        var sessions = app.Services.GetRequiredService<AvatarSessions>();
        var language = QuizLanguageCatalog.Find("sv")!;
        var admitted = new List<AvatarSession>();
        var sockets = new List<WebSocket>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            for (var i = 0; i < 32; i++)
            {
                using var client = await app.Client($"learner-{i}");
                var session = sessions.Create($"learner-{i}", language, "free");
                admitted.Add(session);
                var ws = app.Server.CreateWebSocketClient();
                ws.ConfigureRequest = request =>
                {
                    request.Headers["Cookie"] = client.DefaultRequestHeaders.GetValues("Cookie").Single();
                    request.Headers["Origin"] = "https://localhost";
                };
                var socket = await ws.ConnectAsync(new Uri($"wss://localhost/api/avatar/sessions/{session.Id}/voice"), timeout.Token);
                sockets.Add(socket);
                await Until(socket, "connected", timeout.Token);
                if (i >= AvatarSessions.MaximumLiveSessions)
                {
                    var victim = i - AvatarSessions.MaximumLiveSessions;
                    Assert.True(admitted[victim].Preemption.IsCancellationRequested);
                    Assert.Equal("busy", (await Until(sockets[victim], "ended", timeout.Token)).GetProperty("reason").GetString());
                    var close = await sockets[victim].ReceiveAsync(new byte[1024], timeout.Token);
                    Assert.Equal(WebSocketMessageType.Close, close.MessageType);
                    Assert.Equal(WebSocketCloseStatus.NormalClosure, close.CloseStatus);
                    await tracker.Disposing.Reader.ReadAsync(timeout.Token);
                    // Get distinguishes a still-registered preempted session (410) from a removed one (404).
                    Assert.Equal(410, Assert.Throws<AvatarException>(() =>
                        sessions.Get(admitted[victim].Id, admitted[victim].UserId)).Status);
                }
                if ((i + 1) % 16 == 0)
                {
                    Assert.Equal(i + 1, Volatile.Read(ref tracker.LiveScopes));
                    Assert.Equal(16, admitted.Count(s => !s.Preemption.IsCancellationRequested));
                    Assert.All(admitted, s => Assert.False(s.Lifetime.IsCancellationRequested));
                }
            }
            // Rejection must not preempt another call or replace the caller's current session.
            foreach (var owner in new[] { "newcomer", admitted[^1].UserId })
                Assert.Equal(429, Assert.Throws<AvatarException>(() => sessions.Create(owner, language, "free")).Status);
            Assert.Same(admitted[^1], sessions.Get(admitted[^1].Id, admitted[^1].UserId));
            Assert.Equal(16, admitted.Count(s => !s.Preemption.IsCancellationRequested));
            tracker.Release.TrySetResult();
            while (Volatile.Read(ref tracker.LiveScopes) != 16 || admitted.Take(16).Any(s => !s.Lifetime.IsCancellationRequested))
                await Task.Delay(5, timeout.Token);
            Assert.All(admitted.Take(16), s => Assert.Equal(404,
                Assert.Throws<AvatarException>(() => sessions.Get(s.Id, s.UserId)).Status));
            var next = sessions.Create("newcomer", language, "free");
            sessions.Connect(next.Id, next.UserId);
            sessions.CompleteConnection(next, false);
            Assert.Equal("busy", (await Until(sockets[16], "ended", timeout.Token)).GetProperty("reason").GetString());
            foreach (var socket in sockets.Skip(17))
                await AvatarWire.SendAsync(socket, new { type = "end" }, timeout.Token);
            while (Volatile.Read(ref tracker.LiveScopes) != 0 || admitted.Any(s => !s.Lifetime.IsCancellationRequested))
                await Task.Delay(5, timeout.Token);
        }
        finally
        {
            tracker.Release.TrySetResult();
            foreach (var socket in sockets) socket.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledSaveReleasesArchiveProtection(bool cancel)
    {
        var clock = new FakeTimeProvider(); var sessions = new AvatarSessions(clock);
        var language = QuizLanguageCatalog.Find("sv")!;
        AvatarSession Archive()
        {
            var session = sessions.Create("owner", language, "free");
            sessions.RecordTurn(session, "1", "user", "Keep until this save finishes");
            sessions.Remove(session); clock.Advance(TimeSpan.FromSeconds(1)); return session;
        }
        var target = Archive();
        for (var i = 1; i < AvatarSessions.MaximumConversationsPerUser; i++) Archive();
        var pause = new PausedSave { Fail = !cancel };
        await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).AddInterceptors(pause).Options);
        db.Users.Add(new ApplicationUser { Id = "owner", UserName = "owner" });
        await db.SaveChangesAsync(); pause.Armed = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var save = new AvatarTranscriptService(db, sessions, clock)
            .SaveAsync(target.MemoryId, "owner", "sv", false, cancellation.Token);
        try
        {
            await pause.Entered.Task.WaitAsync(timeout.Token);
            Archive(); // Per-account capacity pressure must also skip the saving archive.
            Assert.Single(sessions.Snapshot(target.MemoryId, "owner", "sv").Turns);
            if (cancel) cancellation.Cancel(); else pause.Release.TrySetResult();
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
            else await Assert.ThrowsAsync<InvalidOperationException>(() => save);
            Assert.Empty(await db.RealtimeTranslationTranscripts.AsNoTracking().ToListAsync(timeout.Token));
            Archive(); // The oldest unsaved archive is reclaimable again after failure.
            Assert.Equal(404, Assert.Throws<AvatarException>(() => sessions.Snapshot(target.MemoryId, "owner", "sv")).Status);
        }
        finally { pause.Release.TrySetResult(); }
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("forget")]
    [InlineData("expire")]
    public void RemovedSessionsKeepConnectionSlotsUntilTransportCompletion(string removal)
    {
        var clock = new FakeTimeProvider(); var sessions = new AvatarSessions(clock);
        var language = QuizLanguageCatalog.Find("sv")!; var connections = new List<AvatarSession>();
        for (var i = 0; i < AvatarSessions.MaximumConnections; i++)
        {
            var session = sessions.Create("owner", language, "free");
            sessions.Connect(session.Id, session.UserId); connections.Add(session);
            if (removal == "forget") sessions.Forget(session.MemoryId, session.UserId);
            else if (removal == "expire") { clock.Advance(TimeSpan.FromMinutes(31)); sessions.Sweep(); }
        }
        Assert.Equal(429, Assert.Throws<AvatarException>(() => sessions.Create("owner", language, "free")).Status);
        sessions.CompleteConnection(connections[0], false);
        var accepted = sessions.Create("newcomer", language, "free");
        sessions.Connect(accepted.Id, accepted.UserId);
        sessions.CompleteConnection(connections[0], false); // Duplicate completion cannot free another slot.
        Assert.Equal(429, Assert.Throws<AvatarException>(() => sessions.Create("another", language, "free")).Status);
        foreach (var connection in connections) sessions.CompleteConnection(connection, false);
        sessions.CompleteConnection(accepted, false);
        var recovered = sessions.Create("recovered", language, "free");
        Assert.Same(recovered, sessions.Connect(recovered.Id, recovered.UserId));
    }

    [Fact]
    public void ConnectingAnEarlierAdmissionRechecksTransportCapacity()
    {
        var sessions = new AvatarSessions(new FakeTimeProvider()); var language = QuizLanguageCatalog.Find("sv")!;
        var waiting = sessions.Create("waiting", language, "free");
        AvatarSession? first = null;
        for (var i = 0; i < AvatarSessions.MaximumConnections; i++)
        {
            var session = sessions.Create("replacing", language, "free");
            sessions.Connect(session.Id, session.UserId); first ??= session;
        }
        Assert.Equal(429, Assert.Throws<AvatarException>(() => sessions.Connect(waiting.Id, waiting.UserId)).Status);
        Assert.Equal(0, waiting.Connected);
        sessions.CompleteConnection(first!, false);
        Assert.Same(waiting, sessions.Connect(waiting.Id, waiting.UserId));
    }

    private static async Task<JsonElement> Until(WebSocket socket, string type, CancellationToken ct)
    {
        while (true)
        {
            using var message = await AvatarWire.ReadAsync(socket, ct, 3 * 1024 * 1024);
            Assert.NotEqual("error", message.RootElement.GetProperty("type").GetString());
            if (message.RootElement.GetProperty("type").GetString() == type) return message.RootElement.Clone();
        }
    }

    private sealed class PausedSave : SaveChangesInterceptor
    {
        public bool Armed;
        public bool Fail;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                if (Fail) throw new InvalidOperationException("Injected database write failure");
            }
            return result;
        }
    }

    private sealed class CleanupTracker
    {
        public int LiveScopes;
        public Channel<bool> Disposing { get; } = Channel.CreateUnbounded<bool>();
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PausedCleanupSpeech : IAvatarSpeech, IAsyncDisposable
    {
        private readonly CleanupTracker _tracker;
        private int _disposed;
        public PausedCleanupSpeech(CleanupTracker tracker)
        {
            _tracker = tracker;
            Interlocked.Increment(ref tracker.LiveScopes);
        }
        public Task<string> ListenAsync(AvatarSession session, string mode, ChannelReader<byte[]> audio,
            Func<string, Task> partial, Func<Task> ready, CancellationToken ct) => throw new NotSupportedException();
        public Task SpeakAsync(AvatarSession session, string text, Func<byte[], Task> audio, CancellationToken ct) => throw new NotSupportedException();
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _tracker.Disposing.Writer.TryWrite(true);
            await _tracker.Release.Task;
            Interlocked.Decrement(ref _tracker.LiveScopes);
        }
    }
}
