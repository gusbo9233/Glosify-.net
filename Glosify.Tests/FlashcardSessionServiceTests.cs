using Glosify.Services;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Glosify.Tests;

public class FlashcardSessionServiceTests
{
    [Fact]
    public void RestartWithAgainCards_PreservesPracticeDirection()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new FlashcardSessionService(cache);
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
