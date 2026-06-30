using Glosify.Data;
using Glosify.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public class WordServiceSentencePracticeTests
{
    [Fact]
    public async Task GetWordsAsync_ReturnsWordsInAddedOrder()
    {
        await using var context = CreateContext();
        var quizId = await SeedQuizAsync(context);
        var firstAddedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        context.Words.AddRange(
            new Word
            {
                Id = "first",
                QuizId = quizId,
                Lemma = "zebra",
                Translation = "zebra",
                CreatedAt = firstAddedAt
            },
            new Word
            {
                Id = "second",
                QuizId = quizId,
                Lemma = "apple",
                Translation = "apple",
                CreatedAt = firstAddedAt.AddMinutes(1)
            });
        await context.SaveChangesAsync();
        var service = new WordService(context);

        var words = await service.GetWordsAsync(quizId);

        Assert.Equal(["zebra", "apple"], words.Select(word => word.Lemma));
    }

    [Fact]
    public async Task GetSentencesAsync_ReturnsSentencesInAddedOrder()
    {
        await using var context = CreateContext();
        var quizId = await SeedQuizAsync(context);
        var firstAddedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        context.QuizSentences.AddRange(
            new QuizSentence
            {
                Id = Guid.NewGuid(),
                QuizId = quizId,
                Text = "Zebra sentence.",
                Translation = "First.",
                CreatedAt = firstAddedAt
            },
            new QuizSentence
            {
                Id = Guid.NewGuid(),
                QuizId = quizId,
                Text = "Apple sentence.",
                Translation = "Second.",
                CreatedAt = firstAddedAt.AddMinutes(1)
            });
        await context.SaveChangesAsync();
        var service = new WordService(context);

        var sentences = await service.GetSentencesAsync(quizId);

        Assert.Equal(
            ["Zebra sentence.", "Apple sentence.", "Esta es una casa."],
            sentences.Select(sentence => sentence.Text));
    }

    [Fact]
    public async Task LoadSentenceCardsAsync_ReturnsStandaloneSentencesAsCards()
    {
        await using var context = CreateContext();
        var quizId = await SeedQuizAsync(context);
        var service = new WordService(context);

        var cards = await service.LoadSentenceCardsAsync(quizId, 10);

        var card = Assert.Single(cards);
        Assert.Equal("Esta es una casa.", card.Lemma);
        Assert.Equal("This is a house.", card.Translation);
    }

    [Fact]
    public async Task GetAvailableSentenceCountAsync_CountsOnlyQuizSentences()
    {
        await using var context = CreateContext();
        var quizId = await SeedQuizAsync(context);
        var otherQuizId = Guid.NewGuid();
        context.Quizzes.Add(new Quiz
        {
            Id = otherQuizId,
            Name = "Other",
            UserId = "user-1",
            SourceLanguage = "English",
            TargetLanguage = "Spanish",
            Language = "Spanish",
            ProcessingStatus = "Ready"
        });
        context.QuizSentences.Add(new QuizSentence
        {
            Id = Guid.NewGuid(),
            QuizId = otherQuizId,
            Text = "Otra frase.",
            Translation = "Another sentence.",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
        var service = new QuizService(context, new TestLanguageContext("Spanish"));

        var count = await service.GetAvailableSentenceCountAsync(quizId);

        Assert.Equal(1, count);
    }

    private static async Task<Guid> SeedQuizAsync(GlosifyContext context)
    {
        var quizId = Guid.NewGuid();
        context.Quizzes.Add(new Quiz
        {
            Id = quizId,
            Name = "Spanish basics",
            UserId = "user-1",
            SourceLanguage = "English",
            TargetLanguage = "Spanish",
            Language = "Spanish",
            ProcessingStatus = "Ready"
        });
        context.QuizSentences.Add(new QuizSentence
        {
            Id = Guid.NewGuid(),
            QuizId = quizId,
            Text = "Esta es una casa.",
            Translation = "This is a house.",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
        return quizId;
    }

    private static GlosifyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new GlosifyContext(options);
    }

    private sealed class TestLanguageContext(string language) : ILanguageContext
    {
        public string? CurrentLanguage => language;
        public bool HasLanguage => !string.IsNullOrWhiteSpace(language);
        public IReadOnlyList<string> SupportedLanguages { get; } = [language];
        public bool TrySetLanguage(string language) => true;
        public void Clear() { }
    }
}
