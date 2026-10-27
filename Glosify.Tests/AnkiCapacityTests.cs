using Glosify.Data;
using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Services.Anki;
using Glosify.Services.Words;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public sealed class AnkiCapacityTests
{
    [Fact]
    public async Task Ten_thousand_cards_are_available_and_overflow_is_atomic()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        await VerifyAsync(db);
    }

    [SqlServerFact]
    public Task SqlServer_ten_thousand_cards_are_available_and_overflow_is_atomic() =>
        SqlServerTestDatabase.RunAsync("anki-capacity", async db =>
        {
            await VerifyAsync(db);
            var collection = await db.AnkiCollections.SingleAsync(item => item.TargetLanguage == "Polish");
            var quiz = await db.Quizzes.SingleAsync(item => item.Name == "Full collection");
            var extraQuiz = await db.Quizzes.SingleAsync(item => item.Name == "Extra word");
            var service = new AnkiCollectionService(db, TimeProvider.System);
            await service.SetMembershipAsync(collection.Id, [], [new(quiz.Id, "words", "word-1")], "capacity-user");
            db.Words.AddRange(new Word { Id = "race-a", QuizId = extraQuiz.Id, Lemma = "race a", Translation = "a" },
                new Word { Id = "race-b", QuizId = extraQuiz.Id, Lemma = "race b", Translation = "b" });
            await db.SaveChangesAsync();
            var options = new DbContextOptionsBuilder<GlosifyContext>().UseSqlServer(db.Database.GetConnectionString()).Options;
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<bool> AddAsync(string id)
            {
                await using var competing = new GlosifyContext(options);
                await start.Task;
                try
                {
                    return await new AnkiCollectionService(competing, TimeProvider.System)
                        .AddItemForLanguageAsync("Polish", "UTC", new(extraQuiz.Id, "words", id), "capacity-user");
                }
                catch (AnkiValidationException) { return false; }
            }
            var first = AddAsync("race-a");
            var second = AddAsync("race-b");
            start.SetResult();
            var results = await Task.WhenAll(first, second);
            Assert.Single(results, added => added);
            Assert.Single(results, added => !added);
            db.ChangeTracker.Clear();
            Assert.Equal(10000, await db.AnkiCards.CountAsync(card => card.IsActive && card.Note.AnkiCollectionId == collection.Id));
        });

    private static async Task VerifyAsync(GlosifyContext db)
    {
        const string userId = "capacity-user";
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "capacity@example.test" });
        var quiz = QuizFor("Polish", "Full collection");
        var extraQuiz = QuizFor("Polish", "Extra word");
        var otherLanguage = QuizFor("French", "Separate collection");
        db.AddRange(quiz, extraQuiz, otherLanguage);
        db.Words.AddRange(Enumerable.Range(0, 5000).Select(i => new Word
        {
            Id = $"word-{i}", QuizId = quiz.Id, Lemma = $"target {i}", Translation = $"source {i}",
        }));
        db.Words.AddRange(
            new Word { Id = "extra", QuizId = extraQuiz.Id, Lemma = "extra", Translation = "additional" },
            new Word { Id = "french", QuizId = otherLanguage.Id, Lemma = "bonjour", Translation = "hello" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = new AnkiCollectionService(db, TimeProvider.System);
        var collection = await service.GetOrCreateAsync(userId, "Polish", "UTC");
        var excludedQuiz = QuizFor("Polish", "Excluded directions");
        db.AddRange(excludedQuiz,
            new Word { Id = "excluded", QuizId = excludedQuiz.Id, Lemma = "excluded word", Translation = "meaning" },
            new QuizSentence { Id = Guid.NewGuid(), QuizId = excludedQuiz.Id, Text = "excluded sentence", Translation = "sentence meaning" });
        await db.SaveChangesAsync();
        Assert.True(await service.AddQuizAsync(new(collection.Id, excludedQuiz.Id, true, false, true, false), userId));
        foreach (var card in await db.AnkiCards.Where(card => card.Note.QuizId == excludedQuiz.Id).ToListAsync())
            Assert.True(await service.RemoveCardAsync(card.Id, userId));

        Assert.True(await service.AddQuizForLanguageAsync("Polish", "UTC", quiz.Id, false, userId));
        Assert.Equal(10000, await ActiveCountAsync());
        var study = new AnkiStudyService(db, service, new Fsrs6AnkiScheduler(), TimeProvider.System);
        Assert.Equal(5000, (await study.GetNextAsync(collection.Id, userId, PracticeDirection.SourceToTarget))!.Card!.NewRemaining);
        Assert.Equal(5000, (await service.ReadCountsAsync(collection.Id, userId))!.New);

        // Widening excluded word/sentence links adds no active cards, even at capacity.
        Assert.True(await service.AddQuizForLanguageAsync("Polish", "UTC", excludedQuiz.Id, true, userId));
        db.ChangeTracker.Clear();
        await service.SyncCollectionAsync(collection.Id);
        Assert.Equal(10000, await ActiveCountAsync());
        var excludedCards = await db.AnkiCards.Where(card => card.Note.QuizId == excludedQuiz.Id).ToListAsync();
        Assert.Equal(4, excludedCards.Count);
        Assert.All(excludedCards, card =>
        {
            Assert.False(card.IsActive);
            Assert.True(card.ExcludedFromQuizLink);
            Assert.True(card.QuizLinkIncluded);
        });

        // Every entry point must reject the whole addition, including its quiz link.
        foreach (var path in new[] { "single", "membership", "assistant", "quiz", "assistant-quiz" })
        {
            var error = await Assert.ThrowsAsync<AnkiValidationException>(async () =>
            {
                switch (path)
                {
                    case "single":
                        await service.AddItemAsync(new(collection.Id, extraQuiz.Id, "words", "extra", true, true), userId);
                        break;
                    case "membership":
                        await service.SetMembershipAsync(collection.Id, [new(extraQuiz.Id, "words", "extra")], [], userId);
                        break;
                    case "assistant":
                        await service.AddItemsAsync(new(collection.Id, extraQuiz.Id, "words", ["extra"], true, true), userId);
                        break;
                    case "quiz":
                        await service.AddQuizForLanguageAsync("Polish", "UTC", extraQuiz.Id, false, userId);
                        break;
                    case "assistant-quiz":
                        await service.LinkQuizAdditiveAsync(new(collection.Id, extraQuiz.Id, true, true, false, false), userId);
                        break;
                }
            });
            Assert.Contains("10,000", error.Message);
            db.ChangeTracker.Clear();
            Assert.Equal(10000, await ActiveCountAsync());
            Assert.False(await db.AnkiNotes.AnyAsync(note => note.WordId == "extra"));
            Assert.False(await db.AnkiQuizLinks.AnyAsync(link => link.QuizId == extraQuiz.Id));
        }

        var wordError = await Assert.ThrowsAsync<AnkiValidationException>(() =>
            new WordService(db, service).AddWordAsync(quiz.Id, "overflow word", "overflow meaning"));
        Assert.Contains("10,000", wordError.Message);
        db.ChangeTracker.Clear();
        Assert.False(await db.Words.AnyAsync(word => word.QuizId == quiz.Id && word.Lemma == "overflow word"));
        Assert.Equal(10000, await ActiveCountAsync());

        // Capacity is per language, not per user.
        Assert.True(await service.AddQuizForLanguageAsync("French", "UTC", otherLanguage.Id, false, userId));
        Assert.Equal(10000, await ActiveCountAsync());
        Assert.Equal(10002, await db.AnkiCards.CountAsync(card => card.IsActive));

        // A duplicate pair consumes no additional slots at the limit.
        db.Words.Add(new Word { Id = "duplicate", QuizId = extraQuiz.Id, Lemma = "target 0", Translation = "source 0" });
        await db.SaveChangesAsync();
        Assert.True(await service.AddItemForLanguageAsync("Polish", "UTC", new(extraQuiz.Id, "words", "duplicate"), userId));
        Assert.Equal(10000, await ActiveCountAsync());

        // Removing two directions frees two slots, while keeping their history.
        Assert.True(await service.SetMembershipAsync(collection.Id,
            [new(extraQuiz.Id, "words", "extra")], [new(quiz.Id, "words", "word-0")], userId));
        Assert.Equal(10000, await ActiveCountAsync());
        Assert.Equal(2, await db.AnkiCards.CountAsync(card => card.Note.WordId == "word-0" && !card.IsActive));
        await service.SyncCollectionAsync(collection.Id);
        Assert.Equal(10000, await ActiveCountAsync());
        await Assert.ThrowsAsync<AnkiValidationException>(() => service.AddItemForLanguageAsync(
            "Polish", "UTC", new(quiz.Id, "words", "word-0"), userId));
        db.ChangeTracker.Clear();
        Assert.Equal(10000, await ActiveCountAsync());

        Task<int> ActiveCountAsync() => db.AnkiCards.CountAsync(card => card.IsActive && card.Note.AnkiCollectionId == collection.Id);
        static Quiz QuizFor(string language, string name) => new()
        {
            Id = Guid.NewGuid(), UserId = userId, Name = name, SourceLanguage = "English",
            TargetLanguage = language, Language = language,
        };
    }
}
