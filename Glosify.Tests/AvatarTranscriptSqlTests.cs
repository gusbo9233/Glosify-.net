using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Avatar;
using Glosify.Services.Language;
using Glosify.Services.RealtimeTranslation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Glosify.Tests;

public sealed class AvatarTranscriptSqlTests
{
    [SqlServerFact]
    public Task ConcurrentSavesDeletionAndResavePreserveOneCompleteTranscript() => SqlServerTestDatabase.RunAsync("avatar_transcripts", async db =>
    {
        db.Users.Add(new ApplicationUser { Id = "owner", UserName = "owner", AutoSaveAvatarConversations = true });
        await db.SaveChangesAsync();
        var sessions = new AvatarSessions(TimeProvider.System);
        var session = sessions.Create("owner", QuizLanguageCatalog.Find("sv")!, "free");
        for (var i = 0; i < 60; i++) sessions.RecordTurn(session, i.ToString(), i % 2 == 0 ? "user" : "assistant", $"Spoken turn {i}");
        var options = new DbContextOptionsBuilder<GlosifyContext>().UseSqlServer(db.Database.GetConnectionString()).Options;
        async Task<AvatarTranscriptStatus> Save(bool automatic)
        {
            await using var writer = new GlosifyContext(options);
            return await new AvatarTranscriptService(writer, sessions, TimeProvider.System).SaveAsync(session.MemoryId, "owner", "sv", automatic, default);
        }
        var saves = await Task.WhenAll(Save(false), Save(true), Save(false));
        Assert.Single(saves.Select(s => s.TranscriptId).Distinct());
        Assert.All(saves, s => Assert.Equal(60, s.SavedTurns));
        Assert.Equal(60, await db.RealtimeTranslationTranscriptSegments.CountAsync());
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var reader = new RealtimeTranslationTranscriptService(db, TimeProvider.System, cache, sessions);
        var id = saves[0].TranscriptId!.Value;
        var page = await reader.GetTextPageAsync(id, "owner", "sv", new(), default);
        Assert.Equal(60, page!.TotalSegments);
        Assert.Equal("You", page.Segments[0].Speaker); Assert.Equal("Rain", page.Segments[1].Speaker);
        Assert.Equal("Spoken turn 59", page.Segments[^1].Text);
        var subtitles = new RealtimeTranslationTranscript
        {
            UserId = "owner", TargetLanguage = "sv", Title = "Subtitles",
            SourceKind = RealtimeTranslationTranscriptSourceKinds.Subtitles,
        };
        db.RealtimeTranslationTranscripts.Add(subtitles);
        await db.SaveChangesAsync();
        using (var held = await sessions.LockTranscriptWritesAsync("owner", default))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            // Subtitle deletion does not wait behind this account's avatar save.
            await reader.DeleteAsync(subtitles.Id, "owner", "sv", timeout.Token);
        }
        async Task DeleteAvatar()
        {
            await using var deletion = new GlosifyContext(options);
            await new RealtimeTranslationTranscriptService(deletion, TimeProvider.System, cache, sessions)
                .DeleteAsync(id, "owner", "sv");
        }
        using (var held = await sessions.LockTranscriptWritesAsync("owner", default))
        {
            var deletion = DeleteAvatar();
            var automaticSave = Save(true);
            held.Dispose();
            await Task.WhenAll(deletion, automaticSave);
        }
        Assert.Empty(await db.RealtimeTranslationTranscriptSegments.ToListAsync());
        var suppressed = await Save(true);
        Assert.Null(suppressed.TranscriptId); Assert.True(suppressed.Suppressed);
        Assert.True(suppressed.Revision > saves.Max(s => s.Revision));
        Assert.Empty(await db.RealtimeTranslationTranscripts.ToListAsync());
        var recreated = await Save(false);
        Assert.NotEqual(id, recreated.TranscriptId); Assert.Equal(60, recreated.SavedTurns);
        Assert.True(recreated.Revision > suppressed.Revision);
        db.Users.Remove(await db.Users.SingleAsync()); await db.SaveChangesAsync();
        Assert.Empty(await db.RealtimeTranslationTranscripts.ToListAsync());
        Assert.Empty(await db.RealtimeTranslationTranscriptSegments.ToListAsync());
    });
}
