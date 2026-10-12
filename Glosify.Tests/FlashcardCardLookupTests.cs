using System.Data.Common;
using System.Security.Claims;
using Glosify.Controllers;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Flashcards;
using Glosify.Services.Quizzes;
using Glosify.Services.Words;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Glosify.Tests;

public sealed class FlashcardCardLookupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lookup_is_one_bounded_owner_scoped_query_and_preserves_exact_card_content(bool sentence)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var commands = new CardReads();
        await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>()
            .UseSqlite(connection).AddInterceptors(commands).Options);
        await db.Database.EnsureCreatedAsync();
        await VerifyLookupAsync(db, commands, sentence);
    }

    [SqlServerFact]
    public Task SqlServer_lookup_preserves_case_sensitive_staleness_checks() =>
        SqlServerTestDatabase.RunAsync("flashcard_lookup", async seed =>
        {
            var commands = new CardReads();
            await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>()
                .UseSqlServer(seed.Database.GetConnectionString()).AddInterceptors(commands).Options);
            await VerifyLookupAsync(db, commands, false);
            await VerifyLookupAsync(db, commands, true);
        });

    private static async Task VerifyLookupAsync(GlosifyContext db, CardReads commands, bool sentence)
    {
        var userId = Guid.NewGuid().ToString("N");
        db.Users.Add(new ApplicationUser { Id = userId, UserName = userId });
        var quiz = new Quiz { Id = Guid.NewGuid(), UserId = userId, Name = "Cards", SourceLanguage = "English", TargetLanguage = "Polish", Language = "Polish" };
        db.Quizzes.Add(quiz);
        var word = new Word { Id = Guid.NewGuid().ToString("N"), QuizId = quiz.Id, Lemma = "Dom", Translation = "House" };
        var phrase = new QuizSentence { Id = Guid.NewGuid(), QuizId = quiz.Id, Text = "  Dom  ", Translation = " House " };
        db.Words.Add(word);
        db.QuizSentences.Add(phrase);
        db.Words.AddRange(Enumerable.Range(0, 200).Select(i => new Word
        {
            Id = Guid.NewGuid().ToString("N"), QuizId = quiz.Id, Lemma = $"Word {i}", Translation = $"Translation {i}"
        }));
        db.QuizSentences.AddRange(Enumerable.Range(0, 200).Select(i => new QuizSentence
        {
            Id = Guid.NewGuid(), QuizId = quiz.Id, Text = $"Sentence {i}", Translation = $"Translation {i}"
        }));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = new WordService(db, null!);
        var id = sentence ? phrase.Id.ToString("N") : word.Id;
        commands.Queries.Clear();
        Assert.True(await service.CardMatchesAsync(quiz.Id, userId, id, "Dom", "House", sentence));
        var query = Assert.Single(commands.Queries);
        Assert.True(query.Contains("LIMIT", StringComparison.Ordinal) || query.Contains("TOP(2)", StringComparison.Ordinal), query);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.False(await service.CardMatchesAsync(quiz.Id, "another-user", id, "Dom", "House", sentence));
        Assert.False(await service.CardMatchesAsync(Guid.NewGuid(), userId, id, "Dom", "House", sentence));
        Assert.False(await service.CardMatchesAsync(quiz.Id, userId, id, "dom", "House", sentence));
        Assert.False(await service.CardMatchesAsync(quiz.Id, userId, id, "Dom", "house", sentence));
        Assert.False(await service.CardMatchesAsync(quiz.Id, userId, "missing-card", "Dom", "House", sentence));
        if (sentence) db.QuizSentences.Remove(phrase); else db.Words.Remove(word);
        await db.SaveChangesAsync();
        Assert.False(await service.CardMatchesAsync(quiz.Id, userId, id, "Dom", "House", sentence));
    }

    [Theory]
    [InlineData(false, false, "text")]
    [InlineData(false, true, "translation")]
    [InlineData(true, false, "translation")]
    [InlineData(true, true, "text")]
    [InlineData(false, false, "deleted")]
    [InlineData(true, true, "deleted")]
    [InlineData(false, true, "owner")]
    [InlineData(true, false, "owner")]
    public async Task Reveal_and_rate_reject_stale_or_inaccessible_cards_without_advancing(bool sentence, bool rate, string change)
    {
        await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var quiz = new Quiz { Id = Guid.NewGuid(), UserId = "user", Name = "Cards", SourceLanguage = "English", TargetLanguage = "Polish", Language = "Polish" };
        var word = new Word { Id = "word", QuizId = quiz.Id, Lemma = "Dom", Translation = "House" };
        var phrase = new QuizSentence { Id = Guid.NewGuid(), QuizId = quiz.Id, Text = "Dom", Translation = "House" };
        db.AddRange(quiz, word, phrase);
        await db.SaveChangesAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sessions = new FlashcardSessionService(cache, new QuizSessionRegistry(cache));
        var session = sessions.StartSession("user", quiz.Id, quiz.Name, "English", "Polish", 10,
            [new FlashcardCardData { Id = sentence ? phrase.Id.ToString("N") : word.Id, Lemma = "Dom", Translation = "House" }],
            practiceItemType: sentence ? "sentences" : "words");
        session.IsAnswerRevealed = rate;
        sessions.SaveSession(session);
        switch (change)
        {
            case "text": if (sentence) phrase.Text = "dom"; else word.Lemma = "dom"; break;
            case "translation": if (sentence) phrase.Translation = "house"; else word.Translation = "house"; break;
            case "deleted": if (sentence) db.Remove(phrase); else db.Remove(word); break;
            case "owner": quiz.UserId = "other-user"; break;
        }
        await db.SaveChangesAsync();
        var controller = new FlashcardQuizController(new QuizService(db, null!, null!), new WordService(db, null!), sessions, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user")], "test"))
            } }
        };
        var result = rate
            ? await controller.Rate(session.SessionId, "good", expectedIndex: 0)
            : await controller.Reveal(session.SessionId, expectedIndex: 0);
        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(0, session.CurrentIndex);
        Assert.Equal(0, session.RememberedCount);
        Assert.Equal(rate, session.IsAnswerRevealed);
        Assert.Null(sessions.FindSession(session.SessionId, "user"));
    }

    [Theory]
    [InlineData(false, "skip", 0, 200)]
    [InlineData(true, "skip", 0, 200)]
    [InlineData(false, "skip", 1, 409)]
    [InlineData(false, "good", 0, 409)]
    [InlineData(true, "good", 0, 200)]
    [InlineData(true, "invalid", 0, 400)]
    public async Task Legacy_skip_can_advance_unrevealed_cards_but_stale_requests_cannot(bool revealed, string rating, int expectedIndex, int expectedStatus)
    {
        await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var quiz = new Quiz { Id = Guid.NewGuid(), UserId = "user", Name = "Cards", SourceLanguage = "English", TargetLanguage = "Polish", Language = "Polish" };
        db.Quizzes.Add(quiz);
        db.Words.Add(new Word { Id = "first", QuizId = quiz.Id, Lemma = "Dom", Translation = "House" });
        db.Words.Add(new Word { Id = "second", QuizId = quiz.Id, Lemma = "Kot", Translation = "Cat" });
        await db.SaveChangesAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sessions = new FlashcardSessionService(cache, new QuizSessionRegistry(cache));
        var session = sessions.StartSession("user", quiz.Id, quiz.Name, "English", "Polish", 2,
            [new FlashcardCardData { Id = "first", Lemma = "Dom", Translation = "House" },
             new FlashcardCardData { Id = "second", Lemma = "Kot", Translation = "Cat" }]);
        session.IsAnswerRevealed = revealed;
        sessions.SaveSession(session);
        var controller = new FlashcardQuizController(new QuizService(db, null!, null!), new WordService(db, null!), sessions, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user")], "test"))
            } }
        };
        var result = await controller.Rate(session.SessionId, rating, expectedIndex: expectedIndex);
        if (expectedStatus == 200)
        {
            Assert.IsType<ViewResult>(result);
            Assert.Equal(1, session.CurrentIndex);
            Assert.False(session.IsAnswerRevealed);
            Assert.Equal(rating == "skip" ? 1 : 0, session.SkippedCount);
            Assert.Equal(rating == "good" ? 1 : 0, session.RememberedCount);
            Assert.Equal(rating == "skip" ? "skip" : "remembered", Assert.Single(session.Ratings).Rating);
        }
        else
        {
            Assert.Equal(expectedStatus, Assert.IsType<ObjectResult>(result).StatusCode);
            Assert.Equal(0, session.CurrentIndex);
            Assert.Equal(revealed, session.IsAnswerRevealed);
            Assert.Empty(session.Ratings);
            Assert.Equal(0, session.SkippedCount);
            Assert.Equal(0, session.RememberedCount);
        }
        Assert.Same(session, sessions.FindSession(session.SessionId, "user"));
    }

    private sealed class CardReads : DbCommandInterceptor
    {
        public List<string> Queries { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) Queries.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
