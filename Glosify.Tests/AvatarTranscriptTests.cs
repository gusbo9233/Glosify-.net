using System.Net;
using System.Net.Http.Json;
using Glosify.Services.Avatar;
using Glosify.Services.Language;
using Glosify.Services.RealtimeTranslation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Glosify.Tests;

public sealed class AvatarTranscriptTests
{
    [Fact]
    public void FullLogSurvivesHistoryTrimmingReconnectAndDuplicateAcknowledgements()
    {
        var clock = new FakeTimeProvider(); var sessions = new AvatarSessions(clock);
        var session = sessions.Create("owner", QuizLanguageCatalog.Find("sv")!, "free");
        for (var i = 0; i < 80; i++)
        {
            sessions.RecordTurn(session, i.ToString(), "user", $"Turn {i}");
            session.History.Add(AvatarConversation.TextTurn("user", $"Turn {i}"));
        }
        sessions.RecordTurn(session, "79", "user", "duplicate");
        sessions.Remember(session); sessions.Remove(session);
        var next = sessions.Create("owner", session.Language, "free", memoryId: session.MemoryId);
        Assert.Equal(24, next.History.Count);
        var snapshot = sessions.Snapshot(next.MemoryId, "owner", "sv");
        Assert.Equal(80, snapshot.Turns.Length);
        Assert.Equal("Turn 0", snapshot.Turns[0].Text);
        sessions.RecordTurn(next, "new", "assistant", "Hej!");
        sessions.RecordTurn(session, "stale", "assistant", "Stale reply");
        Assert.Equal(80, snapshot.Turns.Length); // Snapshot is immutable.
        Assert.Equal("Rain", sessions.Snapshot(next.MemoryId, "owner", "sv").Turns[^1].Speaker);
        Assert.Equal(81, sessions.Snapshot(next.MemoryId, "owner", "sv").Turns.Length);
        Assert.Equal(404, Assert.Throws<AvatarException>(() => sessions.Snapshot(next.MemoryId, "other", "sv")).Status);
        Assert.Equal(404, Assert.Throws<AvatarException>(() => sessions.Snapshot(next.MemoryId, "owner", "en")).Status);
        clock.Advance(TimeSpan.FromHours(8));
        Assert.Equal(410, Assert.Throws<AvatarException>(() => sessions.Snapshot(next.MemoryId, "owner", "sv")).Status);
    }

    [Fact]
    public void CapacityStopsNewTurnsWithoutDiscardingOldContent()
    {
        var sessions = new AvatarSessions(new FakeTimeProvider());
        var session = sessions.Create("owner", QuizLanguageCatalog.Find("sv")!, "free");
        for (var i = 0; i < AvatarSessions.MaximumTranscriptTurns; i++) sessions.RecordTurn(session, i.ToString(), "user", "Hej");
        Assert.Equal(422, Assert.Throws<AvatarException>(() => sessions.CheckTranscriptCapacity(session)).Status);
        Assert.Equal(AvatarSessions.MaximumTranscriptTurns, sessions.Snapshot(session.MemoryId, "owner", "sv").Turns.Length);
    }

    [Fact]
    public async Task SaveUpdateAutoSaveAndDeleteUseOneOwnedTranscript()
    {
        using var app = new AvatarFixture(); using var client = await app.Client("admin"); await app.Antiforgery(client);
        var sessions = app.Services.GetRequiredService<AvatarSessions>();
        var session = sessions.Create("admin", QuizLanguageCatalog.Find("sv")!, "free");
        sessions.RecordTurn(session, "1", "user", "Jag vill ha kaffe.");
        sessions.RecordTurn(session, "1", "assistant", "Vill du ha mjölk?");
        var route = $"/api/avatar/conversations/{session.MemoryId}/transcript";
        var before = await client.GetFromJsonAsync<AvatarTranscriptStatus>(route);
        Assert.False(before!.AutoSave); Assert.Null(before.TranscriptId);
        using var saved = await client.PostAsJsonAsync(route, new { }); saved.EnsureSuccessStatusCode();
        var first = (await saved.Content.ReadFromJsonAsync<AvatarTranscriptStatus>())!;
        Assert.Equal(2, first.SavedTurns);
        sessions.RecordTurn(session, "2", "user", "Ja tack.");
        using var scope = app.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<AvatarTranscriptService>();
        var manual = await service.SaveAsync(session.MemoryId, "admin", "sv", true, default);
        Assert.Equal(2, manual.SavedTurns); Assert.Equal(3, manual.TurnCount);
        (await client.PostAsJsonAsync("/api/avatar/preferences", new { autoSaveConversations = true })).EnsureSuccessStatusCode();
        var updated = await service.SaveAsync(session.MemoryId, "admin", "sv", true, default);
        Assert.Equal(first.TranscriptId, updated.TranscriptId); Assert.Equal(3, updated.SavedTurns);
        var duplicate = await service.SaveAsync(session.MemoryId, "admin", "sv", false, default);
        Assert.Equal(updated, duplicate);
        var reader = scope.ServiceProvider.GetRequiredService<IRealtimeTranslationTranscriptService>();
        var page = await reader.GetTextPageAsync(first.TranscriptId!.Value, "admin", "sv", new(), default);
        Assert.Equal(new[] { "You", "Rain", "You" }, page!.Segments.Select(s => s.Speaker));
        Assert.Null(await reader.GetDetailAsync(first.TranscriptId.Value, "other", "sv", 1, 100));

    }

    [Fact]
    public async Task WritesRequireAntiforgeryAndConversationOwnership()
    {
        using var app = new AvatarFixture(); using var client = await app.Client("admin");
        var session = app.Services.GetRequiredService<AvatarSessions>().Create("other", QuizLanguageCatalog.Find("sv")!, "free");
        var route = $"/api/avatar/conversations/{session.MemoryId}/transcript";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(route, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/avatar/preferences", new { autoSaveConversations = true })).StatusCode);
        await app.Antiforgery(client);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(route, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(route)).StatusCode);
    }
    [Fact]
    public async Task StorageQuotaFailureLeavesSpokenContentAvailableForRetry()
    {
        using var app = new AvatarFixture { Overrides = services => services.Configure<Glosify.Services.Abuse.AbuseOptions>(o => o.Transcripts = 1) };
        using var client = await app.Client("admin"); await app.Antiforgery(client);
        var sessions = app.Services.GetRequiredService<AvatarSessions>();
        var first = sessions.Create("admin", QuizLanguageCatalog.Find("sv")!, "free");
        sessions.RecordTurn(first, "1", "user", "First saved exchange");
        (await client.PostAsJsonAsync($"/api/avatar/conversations/{first.MemoryId}/transcript", new { })).EnsureSuccessStatusCode();
        var next = sessions.Create("admin", first.Language, "free");
        sessions.RecordTurn(next, "1", "user", "Keep this unsaved text");
        var response = await client.PostAsJsonAsync($"/api/avatar/conversations/{next.MemoryId}/transcript", new { });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("resource_quota_exceeded", problem.GetProperty("code").GetString());
        Assert.Equal(problem.GetProperty("detail").GetString(), problem.GetProperty("error").GetString());
        Assert.Equal("Keep this unsaved text", Assert.Single(sessions.Snapshot(next.MemoryId, "admin", "sv").Turns).Text);
        var status = await client.GetFromJsonAsync<AvatarTranscriptStatus>($"/api/avatar/conversations/{next.MemoryId}/transcript");
        Assert.Null(status!.TranscriptId); Assert.Equal(1, status.TurnCount);
    }

}
