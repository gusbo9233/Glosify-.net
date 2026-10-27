using System.Data.Common;
using System.Diagnostics;
using Glosify.Data;
using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Services.Anki;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Glosify.Tests;

public sealed class AnkiMembershipBatchTests(ITestOutputHelper output)
{
    private const string UserId = "anki-batch-user";

    [Fact]
    public async Task Adding_a_whole_quiz_reads_source_words_only_once()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var reads = new NoteReads();
        await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>()
            .UseSqlite(connection).AddInterceptors(reads).Options);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new ApplicationUser { Id = UserId, UserName = "batch@example.test" });
        var quiz = new Quiz { Id = Guid.NewGuid(), UserId = UserId, Name = "Whole quiz",
            SourceLanguage = "English", TargetLanguage = "Polish", Language = "Polish" };
        db.Quizzes.Add(quiz);
        db.Words.AddRange(Enumerable.Range(0, 1000).Select(i => new Word
        {
            Id = $"whole-{i}", QuizId = quiz.Id, Lemma = $"word {i}", Translation = $"meaning {i}",
        }));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        reads.SourceReads = 0;

        Assert.True(await new AnkiCollectionService(db, TimeProvider.System)
            .AddQuizForLanguageAsync("Polish", "UTC", quiz.Id, false, UserId));

        Assert.Equal(1, reads.SourceReads);
        Assert.Equal(2000, await db.AnkiCards.CountAsync(card => card.IsActive));
        Assert.Single(await db.AnkiQuizLinks.ToListAsync());
    }

    [Theory]
    [InlineData(10)]
    [InlineData(2000)]
    public async Task Membership_batches_bound_note_reads_and_preserve_scheduling(int count)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var reads = new NoteReads();
        await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>()
            .UseSqlite(connection).AddInterceptors(reads).Options);
        await db.Database.EnsureCreatedAsync();
        await VerifyBatchAsync(db, reads, count);
    }

    [SqlServerFact]
    public Task SqlServer_membership_batch_saves_and_removes_2000_items_with_bounded_reads() =>
        SqlServerTestDatabase.RunAsync("anki-batch", async database =>
        {
            var reads = new NoteReads();
            await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>()
                .UseSqlServer(database.Database.GetConnectionString()).AddInterceptors(reads).Options);
            await VerifyBatchAsync(db, reads, 2000);
        });

    private async Task VerifyBatchAsync(GlosifyContext db, NoteReads reads, int count)
    {
        db.Users.Add(new ApplicationUser { Id = UserId, UserName = "batch@example.test" });
        var items = new List<AnkiItemKey>();
        foreach (var chunk in Enumerable.Range(0, count).Chunk(1000))
        {
            var quiz = new Quiz
            {
                Id = Guid.NewGuid(), UserId = UserId, Name = "Batch", SourceLanguage = "English",
                TargetLanguage = "Polish", Language = "Polish",
            };
            db.Quizzes.Add(quiz);
            foreach (var i in chunk)
            {
                if (i % 2 == 0)
                {
                    var word = new Word { Id = $"word-{i}", QuizId = quiz.Id, Lemma = $"dom {i}", Translation = $"house {i}" };
                    db.Words.Add(word);
                    items.Add(new(quiz.Id, PracticeItemType.Words, word.Id));
                }
                else
                {
                    var sentence = new QuizSentence { Id = Guid.NewGuid(), QuizId = quiz.Id, Text = $"To jest dom {i}.", Translation = $"This is house {i}." };
                    db.QuizSentences.Add(sentence);
                    items.Add(new(quiz.Id, PracticeItemType.Sentences, sentence.Id.ToString()));
                }
            }
        }
        await db.SaveChangesAsync();
        var service = new AnkiCollectionService(db, TimeProvider.System);
        var collection = await service.GetOrCreateAsync(UserId, "Polish", "UTC");
        db.ChangeTracker.Clear();
        reads.Count = 0;
        var timer = Stopwatch.StartNew();

        Assert.True(await service.SetMembershipAsync(collection.Id, items, [], UserId));

        output.WriteLine($"Added {count} items: {timer.ElapsedMilliseconds} ms; {reads.Count} note SELECTs.");
        Assert.InRange(reads.Count, 1, 5);
        Assert.Equal(count * 2, await db.AnkiCards.CountAsync(card => card.IsActive));
        var scheduled = await db.AnkiCards.SingleAsync(card => card.Note.WordId == "word-0"
            && card.Direction == PracticeDirection.SourceToTarget);
        scheduled.State = AnkiCardStates.Review;
        scheduled.ReviewCount = 4;
        scheduled.Stability = 12;
        scheduled.DueAt = DateTimeOffset.UtcNow.AddDays(5);
        await db.SaveChangesAsync();
        var due = scheduled.DueAt;
        var cardId = scheduled.Id;
        db.ChangeTracker.Clear();
        reads.Count = 0;
        timer.Restart();

        Assert.True(await service.SetMembershipAsync(collection.Id, [], items, UserId));

        output.WriteLine($"Removed {count} items: {timer.ElapsedMilliseconds} ms; {reads.Count} note SELECTs.");
        Assert.InRange(reads.Count, 1, 5);
        Assert.Equal(0, await db.AnkiCards.CountAsync(card => card.IsActive));
        Assert.Equal(count * 2, await db.AnkiCards.CountAsync());
        db.ChangeTracker.Clear();
        Assert.True(await service.SetMembershipAsync(collection.Id, [items[0]], [], UserId));
        var restored = await db.AnkiCards.AsNoTracking().SingleAsync(card => card.Id == cardId);
        Assert.True(restored.IsActive);
        Assert.Equal(AnkiCardStates.Review, restored.State);
        Assert.Equal(4, restored.ReviewCount);
        Assert.Equal(12, restored.Stability);
        Assert.Equal(due, restored.DueAt);
    }

    private sealed class NoteReads : DbCommandInterceptor
    {
        public int Count { get; set; }
        public int SourceReads { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
                && command.CommandText.Contains("AnkiNotes", StringComparison.Ordinal)) Count++;
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
                && command.CommandText.Contains("FROM \"Words\"", StringComparison.OrdinalIgnoreCase)) SourceReads++;
            return ValueTask.FromResult(result);
        }
    }
}
