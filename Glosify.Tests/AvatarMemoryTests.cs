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
            var session = sessions.Create("owner", English, "free");
            sessions.RecordTurn(session, "1", "user", $"Saved {i}");
            sessions.SetSavedTranscript(session.MemoryId, "owner", Guid.NewGuid(), false, 1);
            sessions.Remove(session); ids.Add(session.MemoryId);
        }
        Assert.Equal(429, Assert.Throws<AvatarException>(() => sessions.Create("owner", English, "free", preconnect: true)).Status);
        Assert.Equal("Saved 0", Assert.Single(sessions.Snapshot(ids[0], "owner", "en").Turns).Text);
        var resumed = sessions.Create("owner", English, "free", memoryId: ids[0], preconnect: true);
        sessions.Remove(resumed);
        Assert.Equal("Saved 0", Assert.Single(sessions.Snapshot(ids[0], "owner", "en").Turns).Text);
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
    public void BytePressureReclaimsOnlyFullySavedIdleArchives()
    {
        var sessions = new AvatarSessions(new FakeTimeProvider());
        var first = sessions.Create("owner", English, "free");
        sessions.RecordTurn(first, "1", "user", "Keep unsaved"); sessions.Remove(first);
        Guid savedId = default;
        for (var i = 0; i < 17; i++)
        {
            var session = sessions.Create("owner", English, "free");
            for (var turn = 0; turn < 40; turn++) sessions.RecordTurn(session, turn.ToString(), "user", new string('x', 12_000));
            sessions.SetSavedTranscript(session.MemoryId, "owner", Guid.NewGuid(), false, 40);
            sessions.Remove(session); if (i == 0) savedId = session.MemoryId;
        }
        var next = sessions.Create("owner", English, "free"); sessions.CheckTranscriptCapacity(next);
        Assert.Equal("Keep unsaved", Assert.Single(sessions.Snapshot(first.MemoryId, "owner", "en").Turns).Text);
        Assert.Equal(404, Assert.Throws<AvatarException>(() => sessions.Snapshot(savedId, "owner", "en")).Status);
    }

    [Fact]
    public void FullCacheRefusesNewConversationInsteadOfEvictingUnsavedTurns()
    {
        var sessions = new AvatarSessions(new FakeTimeProvider());
        var ids = new List<Guid>();
        for (var i = 0; i < 128; i++)
        {
            var session = sessions.Create("owner", English, "free");
            sessions.RecordTurn(session, "1", "user", $"Keep {i}"); ids.Add(session.MemoryId); sessions.Remove(session);
        }
        Assert.Equal(429, Assert.Throws<AvatarException>(() => sessions.Create("owner", English, "free")).Status);
        Assert.Equal("Keep 0", Assert.Single(sessions.Snapshot(ids[0], "owner", "en").Turns).Text);
        sessions.SetSavedTranscript(ids[0], "owner", Guid.NewGuid(), false, 1);
        var next = sessions.Create("owner", English, "free");
        Assert.NotEqual(ids[0], next.MemoryId);
        Assert.Equal("Keep 1", Assert.Single(sessions.Snapshot(ids[1], "owner", "en").Turns).Text);
    }

    [Theory]
    [InlineData("new")]
    [InlineData("owner")]
    [InlineData("language")]
    [InlineData("partner")]
    [InlineData("practice")]
    public void RejectedReplacementLeavesActiveCallAndArchiveWriterIntact(string difference)
    {
        var sessions = new AvatarSessions(new FakeTimeProvider());
        var foreign = sessions.Create("other", English, "free");
        sessions.RecordTurn(foreign, "1", "user", "Private"); sessions.Remove(foreign);
        for (var i = 0; i < 126; i++)
        {
            var idle = sessions.Create("owner", English, "free");
            sessions.RecordTurn(idle, "1", "user", $"Keep {i}"); sessions.Remove(idle);
        }
        var active = sessions.Create("owner", English, "free");
        sessions.Connect(active.Id, "owner");
        sessions.RecordTurn(active, "1", "user", "Current call");
        Assert.Equal(429, Assert.Throws<AvatarException>(() => sessions.Create("owner",
            difference == "language" ? QuizLanguageCatalog.Find("sv")! : English,
            difference == "practice" ? "quiz" : "free",
            character: difference == "partner" ? AvatarCharacter.Rowan : AvatarCharacter.Rain,
            memoryId: difference == "new" ? null : difference == "owner" ? foreign.MemoryId : active.MemoryId)).Status);
        Assert.Same(active, sessions.Get(active.Id, "owner"));
        Assert.False(active.Lifetime.IsCancellationRequested);
        sessions.RecordTurn(active, "2", "user", "Still connected");
        Assert.Equal(new[] { "Current call", "Still connected" }, sessions.Snapshot(active.MemoryId, "owner", "en").Turns.Select(t => t.Text));
        var resumed = sessions.Create("owner", English, "free", memoryId: active.MemoryId);
        Assert.True(active.Lifetime.IsCancellationRequested);
        Assert.Equal(active.MemoryId, resumed.MemoryId);
        sessions.RecordTurn(active, "stale", "user", "Late old socket");
        sessions.RecordTurn(resumed, "3", "user", "Accepted replacement");
        Assert.Equal("Accepted replacement", sessions.Snapshot(resumed.MemoryId, "owner", "en").Turns[^1].Text);
        Assert.Equal(3, sessions.Snapshot(resumed.MemoryId, "owner", "en").Turns.Length);
    }

    [Fact]
    public void FullLiveCapacityStillAllowsReplacingOwnConnection()
    {
        var sessions = new AvatarSessions(new FakeTimeProvider());
        var active = Enumerable.Range(0, 16).Select(i => sessions.Create($"owner-{i}", English, "free")).ToArray();
        foreach (var session in active) sessions.Connect(session.Id, session.UserId);
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
