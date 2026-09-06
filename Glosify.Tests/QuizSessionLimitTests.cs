using System.ComponentModel.DataAnnotations;
using Glosify.Data;
using Glosify.Services.Flashcards;
using Glosify.Services.Quizzes;
using Glosify.Services.Typing;
using Glosify.Services.Words;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Glosify.Tests;

public sealed class QuizSessionLimitTests
{
    [Theory]
    [InlineData(150, 150)]
    [InlineData(201, 201)]
    [InlineData(1001, 1000)]
    public async Task CountBasedLoadersUseTheSameSupportedSize(int requested, int expected)
    {
        await using var db = CreateContext();
        var quizId = await SeedAsync(db);
        var typing = new TypingQuizService(db);
        var flashcards = new WordService(db, null!);

        var typingWords = await typing.GetQuizDataAsync(quizId, requested, practiceItemType: PracticeItemType.Words);
        var typingSentences = await typing.GetQuizDataAsync(quizId, requested, practiceItemType: PracticeItemType.Sentences);
        var cards = await flashcards.LoadCardsAsync(quizId, requested);
        var sentenceCards = await flashcards.LoadSentenceCardsAsync(quizId, requested);

        Assert.Equal(expected, typingWords.Words.Count);
        Assert.Equal(expected, typingSentences.Words.Count);
        Assert.Equal(expected, cards.Count);
        Assert.Equal(expected, sentenceCards.Count);
        Assert.Equal(expected, typingWords.Words.Select(word => word.Id).Distinct().Count());
        Assert.Equal(expected, sentenceCards.Select(card => card.Id).Distinct().Count());
        Assert.All(typingWords.Words, word => Assert.StartsWith("word-", word.Id));
        Assert.All(typingSentences.Words, word => Assert.StartsWith("Sentence ", word.Answer));
    }

    [Theory]
    [InlineData(150)]
    [InlineData(201)]
    [InlineData(1000)]
    [InlineData(1001)]
    public void BothModesResumeAndResetTheSameCountTheyStore(int requested)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var db = CreateContext();
        var registry = new QuizSessionRegistry(cache);
        var typing = new TypingSessionService(cache, new TypingQuizService(db), registry);
        var flashcards = new FlashcardSessionService(cache, registry);
        var quizId = Guid.NewGuid();
        var expected = Math.Min(requested, 1000);
        var words = Enumerable.Range(0, expected).Select(i => new TypingWordData
        {
            Id = $"word-{i}", Prompt = $"Prompt {i}", Answer = $"Answer {i}"
        }).ToArray();
        var cards = words.Select(word => new FlashcardCardData
        {
            Id = word.Id, Lemma = word.Answer, Translation = word.Prompt
        }).ToArray();
        var typed = typing.StartSession("user", quizId, "Quiz", "English", "Polish", requested, words);
        var flashed = flashcards.StartSession("user", quizId, "Quiz", "English", "Polish", requested, cards);
        typing.SubmitAnswer(typed, "Answer 0");
        flashcards.ApplyRating(flashed, "good");
        typing.SaveSession(typed);
        flashcards.SaveSession(flashed);

        Assert.Equal(expected, typed.WordCount);
        Assert.Equal(expected, flashed.WordCount);
        var resumedTyping = typing.FindResumableSession("user", quizId, null, null, requested);
        var resumedFlashcards = flashcards.FindResumableSession("user", quizId, null, null, requested);
        Assert.Same(typed, resumedTyping);
        Assert.Same(flashed, resumedFlashcards);
        Assert.Equal(1, resumedTyping!.CurrentIndex);
        Assert.Equal(1, resumedFlashcards!.CurrentIndex);
        Assert.Null(typing.FindResumableSession("user", quizId, null, null, 20));
        Assert.Null(flashcards.FindResumableSession("user", quizId, null, null, 20));

        typing.ResetSession("user", quizId, null, null, requested);
        flashcards.ResetSession("user", quizId, null, null, requested);
        Assert.Null(typing.FindSession(typed.SessionId, "user"));
        Assert.Null(flashcards.FindSession(flashed.SessionId, "user"));
    }

    [Theory]
    [InlineData(201, true)]
    [InlineData(1000, true)]
    [InlineData(1001, false)]
    [InlineData(0, false)]
    public void SettingsValidateTheSupportedSessionSize(int count, bool expected)
    {
        var settings = new QuizSessionSettings { WordCount = count };
        Assert.Equal(expected, Validator.TryValidateObject(settings, new ValidationContext(settings), [], true));
    }

    [Fact]
    public void AllPresentationCapsOnlyCountsBeyondTheSessionLimit()
    {
        var presentation = QuizSettingsPresentation.Create(null, 1500, 201, 1000, "Quiz", "English", "Polish", "Words");
        Assert.Equal(new QuizSettingsLengthOption(1000, 201, true), presentation.AllLength);
        Assert.Equal(new QuizSettingsLengthOption(10, 10, false), presentation.QuickLength);
        Assert.Equal(new QuizSettingsLengthOption(20, 20, false), presentation.StandardLength);
    }

    private static GlosifyContext CreateContext() => new(new DbContextOptionsBuilder<GlosifyContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static async Task<Guid> SeedAsync(GlosifyContext db)
    {
        var id = Guid.NewGuid();
        db.Quizzes.Add(new Quiz
        {
            Id = id, Name = "Large quiz", UserId = "user", SourceLanguage = "English", TargetLanguage = "Polish",
            Language = "Polish", ProcessingStatus = "Ready"
        });
        for (var i = 0; i < 1001; i++)
        {
            db.Words.Add(new Word { Id = $"word-{i}", QuizId = id, Lemma = $"Word {i}", Translation = $"Translation {i}" });
            db.QuizSentences.Add(new QuizSentence
            {
                Id = Guid.NewGuid(), QuizId = id, Text = $"Sentence {i}", Translation = $"Translated sentence {i}"
            });
        }
        await db.SaveChangesAsync();
        return id;
    }
}
