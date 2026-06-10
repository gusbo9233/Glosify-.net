using Microsoft.Extensions.Caching.Memory;

namespace Glosify.Services;

public class TypingSessionService : ITypingSessionService
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(45);
    private readonly IMemoryCache _cache;
    private readonly ITypingQuizService _typingQuizService;

    public TypingSessionService(IMemoryCache cache, ITypingQuizService typingQuizService)
    {
        _cache = cache;
        _typingQuizService = typingQuizService;
    }

    public TypingSessionData StartSession(
        string userId,
        Guid quizId,
        string quizName,
        string sourceLanguage,
        string targetLanguage,
        int wordCount,
        IReadOnlyList<TypingWordData> words)
    {
        return new TypingSessionData
        {
            SessionId = Guid.NewGuid().ToString("N"),
            UserId = userId,
            QuizId = quizId,
            QuizName = quizName,
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
            WordCount = Math.Clamp(wordCount, 1, 100),
            Words = words
        };
    }

    public TypingSessionData? FindSession(string sessionId, string userId)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(sessionId))
            return null;

        return _cache.TryGetValue(CacheKey(sessionId), out TypingSessionData? session)
            && session?.UserId == userId
            ? session
            : null;
    }

    public void SaveSession(TypingSessionData session)
    {
        _cache.Set(
            CacheKey(session.SessionId),
            session,
            new MemoryCacheEntryOptions { SlidingExpiration = SessionLifetime });
    }

    public TypingAnswerResult SubmitAnswer(TypingSessionData session, string userAnswer)
    {
        if (session.CurrentIndex >= session.Words.Count)
        {
            return new TypingAnswerResult();
        }

        var currentWord = session.Words[session.CurrentIndex];
        var isCorrect = _typingQuizService.CheckAnswer(userAnswer, currentWord.Answer);
        if (isCorrect)
        {
            session.CorrectCount++;
        }
        else
        {
            session.IncorrectCount++;
            session.IncorrectWords.Add(currentWord);
        }

        session.CurrentIndex++;
        var nextWord = session.CurrentIndex < session.Words.Count
            ? session.Words[session.CurrentIndex]
            : null;

        return new TypingAnswerResult
        {
            IsCorrect = isCorrect,
            CorrectAnswer = currentWord.Answer,
            ExampleSentence = currentWord.ExampleSentence,
            ExampleTranslation = currentWord.ExampleTranslation,
            NextWord = nextWord
        };
    }

    private static string CacheKey(string sessionId) => $"typing-quiz:{sessionId}";
}
