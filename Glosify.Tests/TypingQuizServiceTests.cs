using Glosify.Data;
using Glosify.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Glosify.Services.Typing;

namespace Glosify.Tests;

public class TypingQuizServiceTests
{
    [Fact]
    public async Task GetQuizDataAsync_SourceToTarget_UsesTranslationPromptAndLemmaAnswer()
    {
        await using var context = CreateContext();
        var quizId = await SeedQuizAsync(context);
        var service = new TypingQuizService(context);

        var data = await service.GetQuizDataAsync(quizId, 1, PracticeDirection.SourceToTarget, PracticeItemType.Words);

        var word = Assert.Single(data.Words);
        Assert.Equal("house", word.Prompt);
        Assert.Equal("casa", word.Answer);
        Assert.Equal("English", data.PromptLanguage);
        Assert.Equal("Spanish", data.AnswerLanguage);
    }

    [Fact]
    public async Task GetQuizDataAsync_TargetToSource_UsesLemmaPromptAndTranslationAnswer()
    {
        await using var context = CreateContext();
        var quizId = await SeedQuizAsync(context);
        var service = new TypingQuizService(context);

        var data = await service.GetQuizDataAsync(quizId, 1, PracticeDirection.TargetToSource, PracticeItemType.Words);

        var word = Assert.Single(data.Words);
        Assert.Equal("casa", word.Prompt);
        Assert.Equal("house", word.Answer);
        Assert.Equal("Spanish", data.PromptLanguage);
        Assert.Equal("English", data.AnswerLanguage);
    }

    [Fact]
    public async Task GetQuizDataAsync_SourceToTargetSentences_UsesTranslationPromptAndTextAnswer()
    {
        await using var context = CreateContext();
        var quizId = await SeedQuizAsync(context);
        var service = new TypingQuizService(context);

        var data = await service.GetQuizDataAsync(quizId, 1, PracticeDirection.SourceToTarget, PracticeItemType.Sentences);

        var sentence = Assert.Single(data.Words);
        Assert.Equal("This is a house.", sentence.Prompt);
        Assert.Equal("Esta es una casa.", sentence.Answer);
        Assert.Equal(PracticeItemType.Sentences, data.PracticeItemType);
    }

    [Fact]
    public async Task GetQuizDataAsync_TargetToSourceSentences_UsesTextPromptAndTranslationAnswer()
    {
        await using var context = CreateContext();
        var quizId = await SeedQuizAsync(context);
        var service = new TypingQuizService(context);

        var data = await service.GetQuizDataAsync(quizId, 1, PracticeDirection.TargetToSource, PracticeItemType.Sentences);

        var sentence = Assert.Single(data.Words);
        Assert.Equal("Esta es una casa.", sentence.Prompt);
        Assert.Equal("This is a house.", sentence.Answer);
        Assert.Equal(PracticeItemType.Sentences, data.PracticeItemType);
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
        context.Words.Add(new Word
        {
            Id = "word-1",
            QuizId = quizId,
            Lemma = "casa",
            Translation = "house"
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
}
