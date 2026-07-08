using Glosify.Services;
using Microsoft.Extensions.Caching.Memory;
using Xunit;
using Glosify.Services.Typing;
using Glosify.Services.Quizzes;

namespace Glosify.Tests;

public class TypingSessionServiceTests
{
    [Fact]
    public void SubmitAnswer_UsesStoredSessionAnswerAndTracksProgress()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var quizService = new StubTypingQuizService();
        var service = new TypingSessionService(cache, quizService, new QuizSessionRegistry(cache));
        var words = new[]
        {
            new TypingWordData { Id = "1", Prompt = "house", Answer = "casa" },
            new TypingWordData { Id = "2", Prompt = "cat", Answer = "gato" }
        };
        var session = service.StartSession(
            "user-1",
            Guid.NewGuid(),
            "Spanish",
            "English",
            "Spanish",
            2,
            words,
            PracticeDirection.TargetToSource,
            PracticeItemType.Sentences);

        service.SaveSession(session);

        var found = service.FindSession(session.SessionId, "user-1");
        var result = service.SubmitAnswer(found!, "wrong");

        Assert.False(result.IsCorrect);
        Assert.Equal("casa", result.CorrectAnswer);
        Assert.Equal("gato", result.NextWord?.Answer);
        Assert.Equal(1, found!.CurrentIndex);
        Assert.Equal(0, found.CorrectCount);
        Assert.Equal(1, found.IncorrectCount);
        Assert.Equal(PracticeDirection.TargetToSource, found.PracticeDirection);
        Assert.Equal(PracticeItemType.Sentences, found.PracticeItemType);
        Assert.Equal("Spanish", found.PromptLanguage);
        Assert.Equal("English", found.AnswerLanguage);
        Assert.Single(found.IncorrectWords);
    }

    [Fact]
    public void FindSession_DoesNotReturnOtherUsersSession()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new TypingSessionService(cache, new StubTypingQuizService(), new QuizSessionRegistry(cache));
        var session = service.StartSession(
            "user-1",
            Guid.NewGuid(),
            "Spanish",
            "English",
            "Spanish",
            1,
            [new TypingWordData { Id = "1", Prompt = "house", Answer = "casa" }]);

        service.SaveSession(session);

        Assert.Null(service.FindSession(session.SessionId, "user-2"));
    }

    private sealed class StubTypingQuizService : ITypingQuizService
    {
        public Task<TypingQuizData> GetQuizDataAsync(Guid quizId, int wordCount, string? practiceDirection = null, string? practiceItemType = null, int rangeStartPercent = 0, int rangeEndPercent = 100, IReadOnlyCollection<string>? wordIds = null)
        {
            throw new NotImplementedException();
        }

        public bool CheckAnswer(string userAnswer, string correctAnswer)
        {
            return userAnswer.Trim().Equals(correctAnswer.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
