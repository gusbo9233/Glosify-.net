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
