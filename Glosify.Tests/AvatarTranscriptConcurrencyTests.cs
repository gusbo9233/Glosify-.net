using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Avatar;
using Glosify.Services.Language;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public sealed class AvatarTranscriptConcurrencyTests
{
    [Fact]
    public async Task DisabledAutoSaveReportsUnsavedTurnsWithoutDatabaseOrWriteLock()
    {
        var sessions = new AvatarSessions(TimeProvider.System);
        var session = sessions.Create("owner", QuizLanguageCatalog.Find("sv")!, "free");
        var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Users.Add(new ApplicationUser { Id = "owner", UserName = "owner" });
        await db.SaveChangesAsync();
        var service = new AvatarTranscriptService(db, sessions, TimeProvider.System);
        await service.InitializePreferenceAsync("owner", default);
        sessions.RecordTurn(session, "1", "user", "This turn stays unsaved.");
        await db.DisposeAsync(); // A database outage must not affect this path.
        using var held = await sessions.LockTranscriptWritesAsync("owner", default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var status = await service.SaveAsync(session.MemoryId, "owner", "sv", true, timeout.Token);
        Assert.Equal(1, status.TurnCount);
        Assert.Equal(0, status.SavedTurns);
        Assert.Null(status.TranscriptId);
        Assert.False(status.AutoSave);
    }

    [Fact]
    public async Task BlockedSaveDoesNotBlockAnotherOwnerAndCancelledWaiterDoesNotStrandLock()
    {
        var sessions = new AvatarSessions(TimeProvider.System);
        var first = sessions.Create("first", QuizLanguageCatalog.Find("sv")!, "free");
        var second = sessions.Create("second", first.Language, "free");
        sessions.RecordTurn(second, "1", "user", "Independent conversation");
        await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Users.AddRange(new ApplicationUser { Id = "first", UserName = "first" }, new ApplicationUser { Id = "second", UserName = "second" });
        await db.SaveChangesAsync();
        using var held = await sessions.LockTranscriptWritesAsync("first", default);
        using var cancelled = new CancellationTokenSource();
        var waiting = sessions.LockTranscriptWritesAsync("first", cancelled.Token).AsTask();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var status = await new AvatarTranscriptService(db, sessions, TimeProvider.System)
            .SaveAsync(second.MemoryId, "second", "sv", false, timeout.Token);
        Assert.Equal(1, status.SavedTurns);
        Assert.Equal("Independent conversation", (await db.RealtimeTranslationTranscriptSegments.SingleAsync()).Text);
        held.Dispose();
        using var recovered = await sessions.LockTranscriptWritesAsync("first", timeout.Token);
    }

    [Fact]
    public async Task DisablingAutoSavePreventsFutureTurnsFromBeingSaved()
    {
        var sessions = new AvatarSessions(TimeProvider.System);
        var session = sessions.Create("owner", QuizLanguageCatalog.Find("sv")!, "free");
        await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Users.Add(new ApplicationUser { Id = "owner", UserName = "owner", AutoSaveAvatarConversations = true });
        await db.SaveChangesAsync();
        var service = new AvatarTranscriptService(db, sessions, TimeProvider.System);
        sessions.RecordTurn(session, "1", "user", "Saved before disabling");
        var first = await service.SaveAsync(session.MemoryId, "owner", "sv", true, default);
        await service.SetPreferenceAsync("owner", false, default);
        sessions.RecordTurn(session, "2", "user", "Must stay unsaved");
        var status = await service.SaveAsync(session.MemoryId, "owner", "sv", true, default);
        Assert.Equal(first.TranscriptId, status.TranscriptId);
        Assert.Equal(2, status.TurnCount);
        Assert.Equal(1, status.SavedTurns);
        Assert.False(status.AutoSave);
        Assert.True(status.Revision > first.Revision);
        var repeated = await service.SaveAsync(session.MemoryId, "owner", "sv", true, default);
        Assert.Equal(status.Revision, repeated.Revision);
        Assert.Equal("Saved before disabling", (await db.RealtimeTranslationTranscriptSegments.SingleAsync()).Text);
    }
}
