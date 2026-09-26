using System.Data.Common;
using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services;
using Glosify.Services.Abuse;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Anki;
using Glosify.Services.Quizzes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class AccountedQuizDeletionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkDeletion_ReleasesExactChargesWithoutPerWordLedgerQueries(bool deleteQuiz)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var commands = new LedgerReads();
        await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>()
            .UseSqlite(connection).AddInterceptors(commands).Options, Options.Create(new AbuseOptions()));
        await db.Database.EnsureCreatedAsync();
        await VerifyDeletionAsync(db, commands, deleteQuiz);
    }

    [SqlServerFact]
    public Task SqlServer_BulkWordDeletion() => VerifySqlServerAsync(false);

    [SqlServerFact]
    public Task SqlServer_QuizDeletion() => VerifySqlServerAsync(true);

    private static Task VerifySqlServerAsync(bool deleteQuiz) =>
        SqlServerTestDatabase.RunAsync("accounted_delete", async seed =>
        {
            var commands = new LedgerReads();
            await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>()
                .UseSqlServer(seed.Database.GetConnectionString()).AddInterceptors(commands).Options,
                Options.Create(new AbuseOptions()));
            await VerifyDeletionAsync(db, commands, deleteQuiz);
        });

    private static async Task VerifyDeletionAsync(GlosifyContext db, LedgerReads commands, bool deleteQuiz)
    {
        db.Add(new ResourceAccountingState { Id = 1, Ready = true });
        db.Users.Add(new ApplicationUser { Id = "owner", UserName = "owner" });
        db.Users.Add(new ApplicationUser { Id = "other", UserName = "other" });
        await db.SaveChangesAsync();
        var quiz = NewQuiz("owner");
        var untouched = NewQuiz("other");
        db.AddRange(quiz, untouched);
        db.Words.AddRange(Enumerable.Range(0, 80).Select(i => new Word
        {
            Id = $"word-{i}", QuizId = quiz.Id, Lemma = $"lemma-{i}", Translation = $"translation-{i}"
        }));
        db.Words.Add(new Word { Id = "untouched", QuizId = untouched.Id, Lemma = "hej", Translation = "hello" });
        // Leave this dependent unloaded during deletion to exercise database cascades.
        db.QuizSentences.Add(new QuizSentence { Id = Guid.NewGuid(), QuizId = quiz.Id, Text = "Hej du", Translation = "Hello you" });
        await db.SaveChangesAsync();
        var before = await db.Set<ResourceEntry>().AsNoTracking().ToListAsync();
        var expectedEntries = before.Where(e => e.UserId == "other"
            || !deleteQuiz && e.EntityType != nameof(Word)).ToDictionary(e => e.Id);
        var expectedUsage = new Dictionary<(string Scope, string Resource), long>();
        foreach (var entry in expectedEntries.Values)
        foreach (var (resource, amount) in ResourceAccounting.Charges(entry.ChargesJson))
        foreach (var scope in resource is "content_bytes" or "pdf_bytes" ? new[] { entry.UserId, ResourceAccounting.Site } : [entry.UserId])
            expectedUsage[(scope, resource)] = expectedUsage.GetValueOrDefault((scope, resource)) + amount;

        db.ChangeTracker.Clear();
        var anki = new AnkiCollectionService(db, TimeProvider.System);
        var quizzes = new QuizService(db, null!, anki);
        commands.Reads = 0;
        if (deleteQuiz)
        {
            Assert.NotNull(await quizzes.DeleteQuizAsync(quiz.Id, "owner"));
        }
        else
        {
            var applier = new ChangeApplier(db, quizzes, new CollectionService(db, anki),
                NullLogger<ChangeApplier>.Instance, anki);
            var changes = Enumerable.Range(0, 80).Select(i => new PendingChange(PendingChangeKinds.DeleteWord,
                JsonSerializer.SerializeToElement(new { word_id = $"word-{i}" }))).ToArray();
            var result = await applier.ApplyAsync(quiz.Id, "owner", changes, default);
            Assert.Equal(80, result.Applied);
        }
        // Bound the expensive ledger reads independently of the number of deleted words.
        Assert.InRange(commands.Reads, 1, 2);
        db.ChangeTracker.Clear();
        Assert.Equal("untouched", (await db.Words.SingleAsync()).Id);
        Assert.Equal(deleteQuiz ? 1 : 2, await db.Quizzes.CountAsync());
        Assert.Equal(deleteQuiz ? 0 : 1, await db.QuizSentences.CountAsync());
        var actualEntries = await db.Set<ResourceEntry>().ToListAsync();
        Assert.Equal(expectedEntries.Keys.Order(), actualEntries.Select(e => e.Id).Order());
        Assert.All(actualEntries, e => Assert.Equal(expectedEntries[e.Id].ChargesJson, e.ChargesJson));
        var actualUsage = (await db.Set<ResourceUsage>().ToListAsync()).ToDictionary(e => (e.Scope, e.Resource), e => e.Used);
        foreach (var key in expectedUsage.Keys.Union(actualUsage.Keys))
            Assert.Equal(expectedUsage.GetValueOrDefault(key), actualUsage.GetValueOrDefault(key));
        if (deleteQuiz)
            Assert.DoesNotContain(("owner", $"quiz:{quiz.Id}"), actualUsage.Keys);
    }

    private static Quiz NewQuiz(string owner) => new()
    {
        Id = Guid.NewGuid(), UserId = owner, Name = "Test quiz", SourceLanguage = "English",
        TargetLanguage = "Swedish", Language = "Swedish", ProcessingStatus = "Ready"
    };

    private sealed class LedgerReads : DbCommandInterceptor
    {
        public int Reads { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && (command.CommandText.Contains("FROM \"ResourceEntry\"", StringComparison.Ordinal)
                    || command.CommandText.Contains("FROM [ResourceEntry]", StringComparison.Ordinal)))
                Reads++;
            return ValueTask.FromResult(result);
        }
    }
}
