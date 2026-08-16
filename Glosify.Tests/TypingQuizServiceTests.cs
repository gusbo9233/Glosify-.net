using Glosify.Data;
using Glosify.Services;
using Glosify.Services.Typing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public class TypingQuizServiceTests
{
    [Theory]
    [InlineData(PracticeDirection.SourceToTarget, "Largest human organ", "Skin")]
    [InlineData(PracticeDirection.TargetToSource, "Skin", "Largest human organ")]
    public async Task Freestyle_uses_prompt_to_answer_by_default_and_reverses_it(
        string direction,
        string expectedPrompt,
        string expectedAnswer)
    {
        await using var context = CreateContext();
        var quizId = await SeedQuizAsync(
            context,
            "Freestyle",
            "Freestyle",
            "Largest human organ",
            "Skin");
        var service = new TypingQuizService(context);

        var data = await service.GetQuizDataAsync(quizId, 1, direction, PracticeItemType.Words);

        var item = Assert.Single(data.Words);
        Assert.Equal(expectedPrompt, item.Prompt);
        Assert.Equal(expectedAnswer, item.Answer);
    }

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

    private static async Task<Guid> SeedQuizAsync(
        GlosifyContext context,
        string sourceLanguage = "English",
        string targetLanguage = "Spanish",
        string prompt = "casa",
        string answer = "house")
    {
        var quizId = Guid.NewGuid();
        context.Quizzes.Add(new Quiz
        {
            Id = quizId,
            Name = "Spanish basics",
            UserId = "user-1",
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
            Language = targetLanguage,
            ProcessingStatus = "Ready"
        });
        context.Words.Add(new Word
        {
            Id = "word-1",
            QuizId = quizId,
            Lemma = prompt,
            Translation = answer
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
