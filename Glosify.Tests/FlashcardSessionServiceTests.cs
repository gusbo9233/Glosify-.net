using Glosify.Services;
using Glosify.Services.Flashcards;
using Glosify.Services.Quizzes;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Glosify.Tests;

public class FlashcardSessionServiceTests
{
    [Theory]
    [InlineData(PracticeDirection.SourceToTarget, "Largest human organ", "Skin")]
    [InlineData(PracticeDirection.TargetToSource, "Skin", "Largest human organ")]
    public void Freestyle_uses_prompt_to_answer_by_default_and_reverses_it(
        string direction,
        string expectedPrompt,
        string expectedAnswer)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new FlashcardSessionService(cache, new QuizSessionRegistry(cache));

        var session = service.StartSession(
            "user-1",
            Guid.NewGuid(),
            "Anatomy",
            "Freestyle",
            "Freestyle",
            1,
            [new FlashcardCardData { Id = "1", Lemma = "Largest human organ", Translation = "Skin" }],
            direction,
            PracticeItemType.Words);

        var card = Assert.Single(session.Cards);
        Assert.Equal(expectedPrompt, card.Prompt);
        Assert.Equal(expectedAnswer, card.Answer);
    }

    [Fact]
    public void RestartWithAgainCards_PreservesPracticeDirection()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new FlashcardSessionService(cache, new QuizSessionRegistry(cache));
        var session = service.StartSession(
            "user-1",
            Guid.NewGuid(),
            "Spanish basics",
            "English",
            "Spanish",
            1,
            [new FlashcardCardData { Id = "1", Lemma = "casa", Translation = "house" }],
            PracticeDirection.TargetToSource,
            PracticeItemType.Sentences);

        service.ApplyRating(session, "again");

        var restarted = service.RestartWithAgainCards(session);

        Assert.Equal(PracticeDirection.TargetToSource, restarted.PracticeDirection);
        Assert.Equal(PracticeItemType.Sentences, restarted.PracticeItemType);
        Assert.Equal("Spanish", restarted.PromptLanguage);
        Assert.Equal("English", restarted.AnswerLanguage);
        var card = Assert.Single(restarted.Cards);
        Assert.Equal("casa", card.Prompt);
        Assert.Equal("house", card.Answer);
    }
}
