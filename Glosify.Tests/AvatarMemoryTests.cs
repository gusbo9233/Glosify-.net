using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Glosify.Services.Avatar;
using Glosify.Services.Language;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Glosify.Tests;

public sealed class AvatarMemoryTests
{
    private static readonly QuizLanguage English = QuizLanguageCatalog.Find("en")!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnusedPreparationsReleaseMemoryWithoutEvictingRetainedConversations(bool expire)
    {
        var clock = new FakeTimeProvider(); var sessions = new AvatarSessions(clock);
        var retained = sessions.Create("owner", English, "free");
        sessions.RecordTurn(retained, "1", "user", "Remember me");
        sessions.SetSavedTranscript(retained.MemoryId, "owner", Guid.NewGuid(), false, 1);
        sessions.Remove(retained);
        for (var i = 0; i < 130; i++)
        {
            var prepared = sessions.Create("owner", English, "free", preconnect: true);
            if (expire) { clock.Advance(TimeSpan.FromSeconds(31)); sessions.Sweep(); }
            else sessions.Remove(prepared);
        }
        Assert.Equal("Remember me", Assert.Single(sessions.Snapshot(retained.MemoryId, "owner", "en").Turns).Text);
    }

    [Fact]
    public void PreparationAtMemoryCapacityCannotEvictAnArchive()
    {
        var sessions = new AvatarSessions(new FakeTimeProvider()); var ids = new List<Guid>();
        for (var i = 0; i < 128; i++)
        {
            var session = sessions.Create($"owner-{i}", English, "free");
            sessions.RecordTurn(session, "1", "user", $"Saved {i}");
            sessions.SetSavedTranscript(session.MemoryId, $"owner-{i}", Guid.NewGuid(), false, 1);
            sessions.Remove(session); ids.Add(session.MemoryId);
        }
        Assert.Equal(429, Assert.Throws<AvatarException>(() => sessions.Create("owner", English, "free", preconnect: true)).Status);
        Assert.Equal("Saved 0", Assert.Single(sessions.Snapshot(ids[0], "owner-0", "en").Turns).Text);
        var resumed = sessions.Create("owner-0", English, "free", memoryId: ids[0], preconnect: true);
        sessions.Remove(resumed);
        Assert.Equal("Saved 0", Assert.Single(sessions.Snapshot(ids[0], "owner-0", "en").Turns).Text);
    }

    [Theory]
    [InlineData("resumed")]
    [InlineData("spoken")]
    [InlineData("saved")]
    public void PreparationCleanupPreservesExistingOrUsedMemory(string content)
    {
        var sessions = new AvatarSessions(new FakeTimeProvider());
        var original = sessions.Create("owner", English, "free"); sessions.Remove(original);
        var prepared = sessions.Create("owner", English, "free", memoryId: content == "resumed" ? original.MemoryId : null, preconnect: true);
        if (content == "spoken") sessions.RecordTurn(prepared, "1", "user", "Keep this turn");
        var transcriptId = Guid.NewGuid();
        if (content == "saved") sessions.SetSavedTranscript(prepared.MemoryId, "owner", transcriptId, false, 0);
        sessions.Remove(prepared);
        var snapshot = sessions.Snapshot(prepared.MemoryId, "owner", "en");
        if (content == "spoken") Assert.Equal("Keep this turn", Assert.Single(snapshot.Turns).Text);
        else if (content == "saved") Assert.Equal(transcriptId, sessions.SavedTranscript(prepared.MemoryId, "owner", "en").Id);
        else Assert.Equal(original.MemoryId, prepared.MemoryId);
    }

    [Fact]
    public void BytePressureReclaimsFullySavedIdleArchivesFirst()
    {
        var sessions = new AvatarSessions(new FakeTimeProvider());
        var first = sessions.Create("owner", English, "free");
        sessions.RecordTurn(first, "1", "user", "Keep unsaved"); sessions.Remove(first);
        Guid savedId = default;
        for (var i = 0; i < 17; i++)
        {
            var session = sessions.Create($"owner-{i}", English, "free");
            for (var turn = 0; turn < 40; turn++) sessions.RecordTurn(session, turn.ToString(), "user", new string('x', 12_000));
            sessions.SetSavedTranscript(session.MemoryId, $"owner-{i}", Guid.NewGuid(), false, 40);
            sessions.Remove(session); if (i == 0) savedId = session.MemoryId;
        }
        var next = sessions.Create("owner", English, "free"); sessions.CheckTranscriptCapacity(next);
        Assert.Equal("Keep unsaved", Assert.Single(sessions.Snapshot(first.MemoryId, "owner", "en").Turns).Text);
        Assert.Equal(404, Assert.Throws<AvatarException>(() => sessions.Snapshot(savedId, "owner-0", "en")).Status);
    }

    [Fact]
    public void BytePressureTakesFromTheAccountRetainingTheMostTextBeforeOthers()
    {
        var clock = new FakeTimeProvider(); var sessions = new AvatarSessions(clock);
        Guid Fill(string owner)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            var session = sessions.Create(owner, English, "free");
            for (var turn = 0; turn < 40; turn++) sessions.RecordTurn(session, turn.ToString(), "user", new string('x', 12_000));
            sessions.Remove(session); return session.MemoryId;
        }
        // Older unsaved transcripts from ordinary learners, then one account holding four.
        var learners = Enumerable.Range(0, 12).Select(i => Fill($"learner-{i}")).ToArray();
        var heavy = Enumerable.Range(0, AvatarSessions.MaximumConversationsPerUser).Select(_ => Fill("heavy")).ToArray();
        var next = sessions.Create("newcomer", English, "free"); sessions.CheckTranscriptCapacity(next);
        Assert.Equal(404, Assert.Throws<AvatarException>(() => sessions.Snapshot(heavy[0], "heavy", "en")).Status);
        Assert.Equal(40, sessions.Snapshot(heavy[1], "heavy", "en").Turns.Length);
        for (var i = 0; i < learners.Length; i++)
            Assert.Equal(40, sessions.Snapshot(learners[i], $"learner-{i}", "en").Turns.Length);
    }

    [Fact]
    public void FullSharedPoolReclaimsAnIdleConversationInsteadOfLockingEveryoneOut()
    {
        var clock = new FakeTimeProvider(); var sessions = new AvatarSessions(clock);
        var ids = new List<Guid>();
        for (var i = 0; i < AvatarSessions.MaximumConversations; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            var session = sessions.Create($"owner-{i}", English, "free");
            sessions.RecordTurn(session, "1", "user", $"Keep {i}"); ids.Add(session.MemoryId); sessions.Remove(session);
        }
        // A fully saved conversation loses nothing, so it goes before older unsaved ones.
        sessions.SetSavedTranscript(ids[5], "owner-5", Guid.NewGuid(), false, 1);
        sessions.Create("newcomer", English, "free");
        Assert.Equal(404, Assert.Throws<AvatarException>(() => sessions.Snapshot(ids[5], "owner-5", "en")).Status);
        Assert.Equal("Keep 0", Assert.Single(sessions.Snapshot(ids[0], "owner-0", "en").Turns).Text);
        sessions.Create("another-newcomer", English, "free");
        Assert.Equal(404, Assert.Throws<AvatarException>(() => sessions.Snapshot(ids[0], "owner-0", "en")).Status);
        Assert.Equal("Keep 1", Assert.Single(sessions.Snapshot(ids[1], "owner-1", "en").Turns).Text);
    }

    [Fact]
    public void FullSharedPoolTakesFromTheAccountsHoldingTheMostBeforeLighterOnes()
    {
        var clock = new FakeTimeProvider(); var sessions = new AvatarSessions(clock);
        var learners = new List<Guid>();
        for (var i = 0; i < 8; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            var session = sessions.Create($"learner-{i}", English, "free");
            sessions.RecordTurn(session, "1", "user", "Mine"); learners.Add(session.MemoryId); sessions.Remove(session);
        }
        // Newer accounts fill the rest of the pool, each up to its own cap.
        var fillers = new List<Guid>();
        for (var i = 0; i < AvatarSessions.MaximumConversations - learners.Count; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            var session = sessions.Create($"filler-{i / AvatarSessions.MaximumConversationsPerUser}", English, "free");
            sessions.RecordTurn(session, "1", "user", "Filler"); fillers.Add(session.MemoryId); sessions.Remove(session);
        }
        sessions.Create("newcomer", English, "free");
        Assert.Equal(404, Assert.Throws<AvatarException>(() => sessions.Snapshot(fillers[0], "filler-0", "en")).Status);
        for (var i = 0; i < learners.Count; i++)
            Assert.Equal("Mine", Assert.Single(sessions.Snapshot(learners[i], $"learner-{i}", "en").Turns).Text);
    }

    [Fact]
    public void AccountAtItsCapReclaimsItsOwnSavedConversationBeforeOlderUnsavedOnes()
    {
        var clock = new FakeTimeProvider(); var sessions = new AvatarSessions(clock);
        var foreign = sessions.Create("other", English, "free");
        sessions.RecordTurn(foreign, "1", "user", "Other account"); sessions.Remove(foreign);
        var ids = new List<Guid>();
        for (var i = 0; i < AvatarSessions.MaximumConversationsPerUser; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            var session = sessions.Create("owner", English, "free");
            sessions.RecordTurn(session, "1", "user", $"Keep {i}");
            if (i == 1) sessions.SetSavedTranscript(session.MemoryId, "owner", Guid.NewGuid(), false, 1);
            ids.Add(session.MemoryId); sessions.Remove(session);
        }
        var next = sessions.Create("owner", English, "free");
        Assert.NotEqual(ids[1], next.MemoryId);
        Assert.Equal(404, Assert.Throws<AvatarException>(() => sessions.Snapshot(ids[1], "owner", "en")).Status);
        // Resuming at the cap adds nothing, so it reclaims nothing.
        Assert.Equal(ids[0], sessions.Create("owner", English, "free", memoryId: ids[0]).MemoryId);
        foreach (var i in new[] { 0, 2, 3 })
            Assert.Equal($"Keep {i}", Assert.Single(sessions.Snapshot(ids[i], "owner", "en").Turns).Text);
        Assert.Equal("Other account", Assert.Single(sessions.Snapshot(foreign.MemoryId, "other", "en").Turns).Text);
    }

    [Fact]
    public void AccountAtItsCapDropsItsOldestIdleUnsavedConversationButNeverTheLiveOne()
    {
        var clock = new FakeTimeProvider(); var sessions = new AvatarSessions(clock);
        var ids = new List<Guid>();
        for (var i = 0; i < AvatarSessions.MaximumConversationsPerUser - 1; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            var idle = sessions.Create("owner", English, "free");
            sessions.RecordTurn(idle, "1", "user", $"Unsaved {i}"); ids.Add(idle.MemoryId); sessions.Remove(idle);
        }
        // Saved conversations are reclaimed first, so only the live guard protects this one.
        var live = sessions.Create("owner", English, "free"); sessions.Connect(live.Id, "owner");
        sessions.RecordTurn(live, "1", "user", "Current call");
        sessions.SetSavedTranscript(live.MemoryId, "owner", Guid.NewGuid(), false, 1);
        sessions.Create("owner", English, "free");
        Assert.Equal(404, Assert.Throws<AvatarException>(() => sessions.Snapshot(ids[0], "owner", "en")).Status);
        Assert.Equal("Current call", Assert.Single(sessions.Snapshot(live.MemoryId, "owner", "en").Turns).Text);
        Assert.Equal("Unsaved 1", Assert.Single(sessions.Snapshot(ids[1], "owner", "en").Turns).Text);
        Assert.Equal("Unsaved 2", Assert.Single(sessions.Snapshot(ids[2], "owner", "en").Turns).Text);
    }

    [Fact]
    public void PreparationAtTheAccountCapCannotReclaimAConversation()
    {
        var clock = new FakeTimeProvider(); var sessions = new AvatarSessions(clock);
        var ids = new List<Guid>();
        for (var i = 0; i < AvatarSessions.MaximumConversationsPerUser; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            var session = sessions.Create("owner", English, "free");
            sessions.RecordTurn(session, "1", "user", $"Saved {i}");
            sessions.SetSavedTranscript(session.MemoryId, "owner", Guid.NewGuid(), false, 1);
            ids.Add(session.MemoryId); sessions.Remove(session);
        }
        Assert.Equal(429, Assert.Throws<AvatarException>(() => sessions.Create("owner", English, "free", preconnect: true)).Status);
        Assert.All(ids, id => Assert.Single(sessions.Snapshot(id, "owner", "en").Turns));
        sessions.Create("owner", English, "free");
        Assert.Equal(404, Assert.Throws<AvatarException>(() => sessions.Snapshot(ids[0], "owner", "en")).Status);
    }

    [Fact]
    public void ReplacingAnUnusedPreparationAtTheCapKeepsEveryConversation()
    {
        var clock = new FakeTimeProvider(); var sessions = new AvatarSessions(clock);
        var ids = new List<Guid>();
        for (var i = 0; i < AvatarSessions.MaximumConversationsPerUser - 1; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            var idle = sessions.Create("owner", English, "free");
            sessions.RecordTurn(idle, "1", "user", $"Unsaved {i}"); ids.Add(idle.MemoryId); sessions.Remove(idle);
        }
        var prepared = sessions.Create("owner", English, "free", preconnect: true);
        sessions.Create("owner", English, "free");
        Assert.True(prepared.Lifetime.IsCancellationRequested);
        Assert.Equal(404, Assert.Throws<AvatarException>(() => sessions.Snapshot(prepared.MemoryId, "owner", "en")).Status);
        for (var i = 0; i < ids.Count; i++)
            Assert.Equal($"Unsaved {i}", Assert.Single(sessions.Snapshot(ids[i], "owner", "en").Turns).Text);
    }

    [Theory]
    [InlineData("new")]
    [InlineData("owner")]
    [InlineData("language")]
    [InlineData("partner")]
    [InlineData("practice")]
    public void ReplacementAtFullCapacityKeepsTheActiveCallsConversation(string difference)
    {
        var clock = new FakeTimeProvider(); var sessions = new AvatarSessions(clock);
        // The active call is the oldest conversation in a full pool, but live ones never qualify.
        var active = sessions.Create("owner", English, "free");
        sessions.Connect(active.Id, "owner");
        sessions.RecordTurn(active, "1", "user", "Current call");
        clock.Advance(TimeSpan.FromSeconds(1));
        var foreign = sessions.Create("other", English, "free");
        sessions.RecordTurn(foreign, "1", "user", "Private"); sessions.Remove(foreign);
        for (var i = 0; i < AvatarSessions.MaximumConversations - 2; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            var idle = sessions.Create($"idle-{i}", English, "free");
            sessions.RecordTurn(idle, "1", "user", $"Keep {i}"); sessions.Remove(idle);
        }
        var replacement = sessions.Create("owner",
            difference == "language" ? QuizLanguageCatalog.Find("sv")! : English,
            difference == "practice" ? "quiz" : "free",
            character: difference == "partner" ? AvatarCharacter.Rowan : AvatarCharacter.Rain,
            memoryId: difference == "new" ? null : difference == "owner" ? foreign.MemoryId : active.MemoryId);
        Assert.NotEqual(active.MemoryId, replacement.MemoryId);
        Assert.Empty(replacement.History);
        Assert.True(active.Lifetime.IsCancellationRequested);
        Assert.Equal("Current call", Assert.Single(sessions.Snapshot(active.MemoryId, "owner", "en").Turns).Text);
        var resumed = sessions.Create("owner", English, "free", memoryId: active.MemoryId);
        Assert.Equal(active.MemoryId, resumed.MemoryId);
        sessions.RecordTurn(active, "stale", "user", "Late old socket");
        sessions.RecordTurn(resumed, "2", "user", "Accepted replacement");
        Assert.Equal(new[] { "Current call", "Accepted replacement" }, sessions.Snapshot(resumed.MemoryId, "owner", "en").Turns.Select(t => t.Text));
    }

    [Fact]
    public void FullLivePoolDisplacesTheSessionLongestWithoutBilledConversation()
    {
        var clock = new FakeTimeProvider(); var sessions = new AvatarSessions(clock);
        var live = Enumerable.Range(0, AvatarSessions.MaximumLiveSessions).Select(i => sessions.Create($"owner-{i}", English, "free")).ToArray();
        foreach (var session in live) sessions.Connect(session.Id, session.UserId);
        // Only billed conversation keeps a slot; these two last spoke longest ago.
        clock.Advance(TimeSpan.FromSeconds(90));
        live[3].KeepActiveUntil(clock.GetUtcNow().AddSeconds(-30));
        live[5].KeepActiveUntil(clock.GetUtcNow().AddSeconds(-20));
        foreach (var session in live.Where((_, i) => i is not (3 or 5))) session.KeepActiveUntil(clock.GetUtcNow().AddSeconds(60));
        sessions.Disconnect(live[5]);
        Assert.Equal(429, Assert.Throws<AvatarException>(() => sessions.Create("newcomer", English, "free", preconnect: true)).Status);
        Assert.False(live[3].Preemption.IsCancellationRequested);

        var admitted = sessions.Create("newcomer", English, "free");
        // A connected session is told why before it closes; its conversation stays resumable.
        Assert.True(live[3].Preemption.IsCancellationRequested);
        Assert.Equal(410, Assert.Throws<AvatarException>(() => sessions.Get(live[3].Id, "owner-3")).Status);
        // Resuming takes the next idle slot; a disconnected session is removed at once.
        var resumed = sessions.Create("owner-3", English, "free", memoryId: live[3].MemoryId);
        Assert.Equal(live[3].MemoryId, resumed.MemoryId);
        Assert.True(live[5].Lifetime.IsCancellationRequested);
        Assert.Equal(404, Assert.Throws<AvatarException>(() => sessions.Get(live[5].Id, "owner-5")).Status);
        Assert.All(live.Where((_, i) => i is not (3 or 5)), session => Assert.Same(session, sessions.Get(session.Id, session.UserId)));
        Assert.Same(admitted, sessions.Get(admitted.Id, "newcomer"));
        // Once every learner is speaking, a newcomer waits instead of interrupting anyone.
        admitted.KeepActiveUntil(clock.GetUtcNow().AddSeconds(15)); resumed.KeepActiveUntil(clock.GetUtcNow().AddSeconds(15));
        Assert.Equal(429, Assert.Throws<AvatarException>(() => sessions.Create("late", English, "free")).Status);
    }

    [Fact]
    public void FullLiveCapacityStillAllowsReplacingOwnConnection()
    {
        var clock = new FakeTimeProvider(); var sessions = new AvatarSessions(clock);
        var active = Enumerable.Range(0, AvatarSessions.MaximumLiveSessions).Select(i => sessions.Create($"owner-{i}", English, "free")).ToArray();
        foreach (var session in active) { sessions.Connect(session.Id, session.UserId); session.KeepActiveUntil(clock.GetUtcNow().AddMinutes(1)); }
        Assert.Equal(429, Assert.Throws<AvatarException>(() => sessions.Create("another-owner", English, "free")).Status);
        Assert.All(active, session => Assert.False(session.Lifetime.IsCancellationRequested));
        var replacement = sessions.Create("owner-0", English, "free", memoryId: active[0].MemoryId);
        Assert.Equal(active[0].MemoryId, replacement.MemoryId);
        Assert.True(active[0].Lifetime.IsCancellationRequested);
        Assert.All(active.Skip(1), session => Assert.Same(session, sessions.Get(session.Id, session.UserId)));
    }

    [Fact]
    public void ANewVisitRestoresOwnedContextWithoutReusingTheExpiredConnection()
    {
        var clock = new FakeTimeProvider(); var sessions = new AvatarSessions(clock);
        var first = sessions.Create("owner", English, "free");
        first.History.Add(AvatarConversation.TextTurn("user", "My dog's name is Otto."));
        sessions.Remember(first); sessions.Remove(first);
        clock.Advance(TimeSpan.FromMinutes(40));
        var next = sessions.Create("owner", English, "free", memoryId: first.MemoryId);
        Assert.NotEqual(first.Id, next.Id);
        Assert.Equal(first.MemoryId, next.MemoryId);
        Assert.Contains("Otto", Assert.Single(next.History).ContentJson);
        Assert.NotSame(first.History, next.History);
        Assert.Equal(clock.GetUtcNow().AddMinutes(30), next.ExpiresAt);
        Assert.Equal(404, Assert.Throws<AvatarException>(() => sessions.Get(first.Id, "owner")).Status);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("language")]
    [InlineData("partner")]
    [InlineData("practice")]
    public void AReferenceCannotImportAnotherAccountOrContextsHistory(string difference)
    {
        var sessions = new AvatarSessions(new FakeTimeProvider());
        var first = sessions.Create("owner", English, "free");
        first.History.Add(AvatarConversation.TextTurn("user", "Private context"));
        sessions.Remember(first); sessions.Remove(first);
        var next = sessions.Create(difference == "owner" ? "other" : "owner",
            difference == "language" ? QuizLanguageCatalog.Find("sv")! : English,
            difference == "practice" ? "quiz" : "free",
            character: difference == "partner" ? AvatarCharacter.Rowan : AvatarCharacter.Rain,
            memoryId: first.MemoryId);
        Assert.Empty(next.History);
        Assert.NotEqual(first.MemoryId, next.MemoryId);
    }

    [Fact]
    public void ForgetIsOwnerScopedAndLateCleanupCannotRestoreForgottenMemory()
    {
        var sessions = new AvatarSessions(new FakeTimeProvider());
        var first = sessions.Create("owner", English, "free");
        first.History.Add(AvatarConversation.TextTurn("user", "Private context")); sessions.Remember(first);
        sessions.Forget(first.MemoryId, "other");
        Assert.False(first.Lifetime.IsCancellationRequested);
        sessions.Remove(first);
        var next = sessions.Create("owner", English, "free", memoryId: first.MemoryId);
        Assert.Single(next.History);
        sessions.Forget(first.MemoryId, "owner");
        Assert.True(next.Lifetime.IsCancellationRequested);
        sessions.Remember(first); sessions.Remember(next);
        Assert.Empty(sessions.Create("owner", English, "free", memoryId: first.MemoryId).History);
    }

    [Fact]
    public void LateOldConnectionCleanupCannotOverwriteTheNewVisitsHistory()
    {
        var sessions = new AvatarSessions(new FakeTimeProvider());
        var first = sessions.Create("owner", English, "free");
        first.History.Add(AvatarConversation.TextTurn("user", "Earlier")); sessions.Remember(first); sessions.Remove(first);
        var next = sessions.Create("owner", English, "free", memoryId: first.MemoryId);
        next.History.Add(AvatarConversation.TextTurn("user", "Newer")); sessions.Remember(next);
        sessions.Remove(next); sessions.Remember(first);
        Assert.Contains(sessions.Create("owner", English, "free", memoryId: first.MemoryId).History,
            turn => turn.ContentJson.Contains("Newer", StringComparison.Ordinal));
    }

    [Fact]
    public void MemoryExpiresAfterEightHoursOfInactivityAndHasBoundedHistory()
    {
        var clock = new FakeTimeProvider(); var sessions = new AvatarSessions(clock);
        var first = sessions.Create("owner", English, "free");
        for (var i = 0; i < 40; i++) first.History.Add(AvatarConversation.TextTurn("user", $"Turn {i}:" + new string('x', 1100)));
        sessions.Remember(first); sessions.Remove(first);
        var next = sessions.Create("owner", English, "free", memoryId: first.MemoryId);
        Assert.InRange(next.History.Count, 1, 24);
        Assert.InRange(next.History.Sum(turn => turn.ContentJson.Length), 1, 24000);
        Assert.Contains("Turn 39:", next.History[^1].ContentJson);
        sessions.Remove(next); clock.Advance(TimeSpan.FromHours(8));
        Assert.Empty(sessions.Create("owner", English, "free", memoryId: first.MemoryId).History);
    }

    [Fact]
    public void ClaimingMemoryJustBeforeExpiryKeepsItForTheActiveSession()
    {
        var clock = new FakeTimeProvider(); var sessions = new AvatarSessions(clock);
        var first = sessions.Create("owner", English, "free");
        first.History.Add(AvatarConversation.TextTurn("user", "Earlier")); sessions.Remember(first); sessions.Remove(first);
        clock.Advance(TimeSpan.FromHours(8) - TimeSpan.FromMinutes(1));
        var next = sessions.Create("owner", English, "free", memoryId: first.MemoryId);
        sessions.Connect(next.Id, "owner");
        clock.Advance(TimeSpan.FromMinutes(2)); sessions.Sweep();
        next.History.Add(AvatarConversation.TextTurn("user", "Newer")); sessions.Remember(next); sessions.Remove(next);
        var later = sessions.Create("owner", English, "free", memoryId: first.MemoryId);
        Assert.Equal(first.MemoryId, later.MemoryId);
        Assert.Contains(later.History, turn => turn.ContentJson.Contains("Newer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartReturnsMemoryReferenceAndForgetRequiresAntiforgery()
    {
        using var app = new AvatarFixture(); using var client = await app.Client("admin");
        await app.Antiforgery(client);
        using var start = await client.PostAsJsonAsync("/api/avatar/sessions", new { });
        start.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(await start.Content.ReadAsStringAsync());
        var id = payload.RootElement.GetProperty("sessionId").GetGuid();
        var memoryId = payload.RootElement.GetProperty("memoryId").GetGuid();
        var sessions = app.Services.GetRequiredService<AvatarSessions>(); var session = sessions.Get(id, "admin");
        session.History.Add(AvatarConversation.TextTurn("user", "Remember this visit")); sessions.Remember(session);
        await client.PostAsJsonAsync($"/api/avatar/sessions/{id}/end", new { });
        using var resumed = await client.PostAsJsonAsync("/api/avatar/sessions", new { memoryId });
        resumed.EnsureSuccessStatusCode();
        using var restored = JsonDocument.Parse(await resumed.Content.ReadAsStringAsync());
        Assert.Equal(memoryId, restored.RootElement.GetProperty("memoryId").GetGuid());
        Assert.Equal(1, restored.RootElement.GetProperty("rememberedTurns").GetInt32());
        using var unprotected = await app.Client("admin");
        Assert.Equal(HttpStatusCode.BadRequest, (await unprotected.PostAsJsonAsync($"/api/avatar/memories/{memoryId}/forget", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/api/avatar/memories/{memoryId}/forget", new { })).StatusCode);
        Assert.Empty(sessions.Create("admin", QuizLanguageCatalog.Find("sv")!, session.Practice, memoryId: memoryId).History);
    }
}
