using Glosify.Data;
using Glosify.Migrations;
using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Services.Abuse;
using Glosify.Services.Anki;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class SimplifyAnkiMigrationTests
{
    [Fact]
    public async Task Cleanup_rebuilds_accounting_without_retired_journal_or_anki_charges()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>()
            .UseSqlite(connection).Options, Options.Create(new AbuseOptions()));
        await db.Database.EnsureCreatedAsync();
        await VerifyCleanupAsync(db);
    }

    [SqlServerFact]
    public Task SqlServer_cleanup_rebuilds_accounting_without_retired_journal_or_anki_charges() =>
        SqlServerTestDatabase.RunAsync("anki-cleanup", async database =>
        {
            await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>()
                .UseSqlServer(database.Database.GetConnectionString()).Options, Options.Create(new AbuseOptions()));
            await VerifyCleanupAsync(db);
        });

    private static async Task VerifyCleanupAsync(GlosifyContext db)
    {
        const string userId = "anki-cleanup-user";
        db.Add(new ResourceAccountingState { Id = 1, Ready = true });
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "cleanup@example.test" });
        await db.SaveChangesAsync();
        var quiz = new Quiz { Id = Guid.NewGuid(), UserId = userId, Name = "Retained quiz",
            SourceLanguage = "English", TargetLanguage = "Polish", Language = "Polish" };
        var thread = new AssistantThread { Id = Guid.NewGuid(), UserId = userId, Title = "Retained chat" };
        var run = new AssistantRun { Id = Guid.NewGuid(), UserId = userId, ThreadId = thread.Id };
        db.AddRange(quiz, thread, run, new Word { Id = "cleanup-word", QuizId = quiz.Id, Lemma = "dom", Translation = "house" });
        var sequence = 0;
        foreach (var kind in new[] { "anki_create", "anki_rename", "anki_delete", "rename_quiz" })
            db.AssistantChanges.Add(new AssistantChange { Id = Guid.NewGuid(), UserId = userId, RunId = run.Id,
                Kind = kind, Sequence = sequence++, BeforeJson = new string('x', 2000) });
        await db.SaveChangesAsync();
        Assert.True(await new AnkiCollectionService(db, TimeProvider.System)
            .AddQuizForLanguageAsync("Polish", "UTC", quiz.Id, false, userId));
        var retainedEntryIds = await db.Set<ResourceEntry>().Where(entry => !entry.EntityType.StartsWith("Anki")
            && entry.EntityType != nameof(AssistantChange)).Select(entry => entry.Id).ToListAsync();
        var retainedJournal = await db.AssistantChanges.SingleAsync(change => change.Kind == "rename_quiz");
        retainedEntryIds.Add(ResourceAccounting.Key(db.Entry(retainedJournal)));
        var retainedBytes = (await db.Set<ResourceEntry>().Where(entry => retainedEntryIds.Contains(entry.Id)).ToListAsync())
            .Sum(entry => ResourceAccounting.Charges(entry.ChargesJson)["content_bytes"]);
        db.ChangeTracker.Clear();

        // Exercise the migration's data operations against populated current tables.
        // Schema alterations are covered by the SQL Server migration/deployment checks.
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            foreach (var operation in new SimplifyAnki().UpOperations.OfType<SqlOperation>())
            {
                var sql = db.Database.IsSqlite() ? operation.Sql.Replace("N'", "'", StringComparison.Ordinal) : operation.Sql;
                await db.Database.ExecuteSqlRawAsync(sql);
            }
            await transaction.CommitAsync();
        }

        Assert.Equal("rename_quiz", (await db.AssistantChanges.SingleAsync()).Kind);
        Assert.Empty(await db.AnkiCollections.ToListAsync());
        Assert.False((await db.Set<ResourceAccountingState>().SingleAsync()).Ready);
        db.Quizzes.Add(new Quiz { Id = Guid.NewGuid(), UserId = userId, Name = "Blocked during rebuild", Language = "Polish" });
        Assert.Equal("accounting_initializing", (await Assert.ThrowsAsync<ResourceQuotaException>(() => db.SaveChangesAsync())).Resource);
        using var services = new ServiceCollection().BuildServiceProvider();
        await ResourceMaintenanceService.RebuildAsync(db, services, default);
        // Routine reconciliation must not reintroduce any of the deleted charges.
        await ResourceMaintenanceService.ReconcileCountersAsync(db, default);
        db.ChangeTracker.Clear();

        Assert.True((await db.Set<ResourceAccountingState>().SingleAsync()).Ready);
        Assert.Equal(retainedEntryIds.Order(), (await db.Set<ResourceEntry>().Select(entry => entry.Id).ToListAsync()).Order());
        Assert.Equal(retainedBytes, (await db.Set<ResourceUsage>().SingleAsync(row => row.Scope == userId && row.Resource == "content_bytes")).Used);
        Assert.Equal(retainedBytes, (await db.Set<ResourceUsage>().SingleAsync(row => row.Scope == ResourceAccounting.Site && row.Resource == "content_bytes")).Used);
        Assert.False(await db.Set<ResourceUsage>().AnyAsync(row => row.Resource == "collections" && row.Used != 0));
        Assert.True(await new AnkiCollectionService(db, TimeProvider.System)
            .AddQuizForLanguageAsync("Polish", "UTC", quiz.Id, false, userId));
        Assert.Equal(2, await db.AnkiCards.CountAsync(card => card.IsActive));
    }
}
