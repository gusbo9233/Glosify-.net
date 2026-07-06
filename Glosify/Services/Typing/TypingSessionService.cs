using Microsoft.Extensions.Caching.Memory;
using Glosify.Services.Quizzes;

namespace Glosify.Services.Typing;

public class TypingSessionService : ITypingSessionService
{
    private const string Mode = "typing";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(45);
    private readonly IMemoryCache _cache;
    private readonly ITypingQuizService _typingQuizService;
    private readonly IQuizSessionRegistry _registry;

    public TypingSessionService(IMemoryCache cache, ITypingQuizService typingQuizService, IQuizSessionRegistry registry)
    {
        _cache = cache;
        _typingQuizService = typingQuizService;
        _registry = registry;
    }

    public TypingSessionData StartSession(
        string userId,
        Guid quizId,
        string quizName,
        string sourceLanguage,
        string targetLanguage,
        int wordCount,
        IReadOnlyList<TypingWordData> words,
        string? practiceDirection = null,
        string? practiceItemType = null)
    {
        var normalizedDirection = PracticeDirection.Normalize(practiceDirection);
        var normalizedItemType = PracticeItemType.Normalize(practiceItemType);
        return new TypingSessionData
        {
            SessionId = Guid.NewGuid().ToString("N"),
            UserId = userId,
            QuizId = quizId,
            QuizName = quizName,
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
            PracticeDirection = normalizedDirection,
            PromptLanguage = PracticeDirection.PromptLanguage(normalizedDirection, sourceLanguage, targetLanguage),
            AnswerLanguage = PracticeDirection.AnswerLanguage(normalizedDirection, sourceLanguage, targetLanguage),
            PracticeItemType = normalizedItemType,
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

        if (IsComplete(session))
        {
            _registry.Deregister(session.UserId, session.SessionId);
        }
        else
        {
            _registry.Register(new ActiveQuizSession
            {
                UserId = session.UserId,
                SessionId = session.SessionId,
                Mode = Mode,
                QuizId = session.QuizId,
                PracticeDirection = session.PracticeDirection,
                PracticeItemType = session.PracticeItemType,
                WordCount = session.WordCount,
                CacheKey = CacheKey(session.SessionId)
            });
        }
    }

    public TypingSessionData? FindResumableSession(string userId, Guid quizId, string? practiceDirection, string? practiceItemType, int wordCount)
    {
        var active = _registry.FindActive(
            userId,
            Mode,
            quizId,
            PracticeDirection.Normalize(practiceDirection),
            PracticeItemType.Normalize(practiceItemType),
            Math.Clamp(wordCount, 1, 100));

        return active == null ? null : FindSession(active.SessionId, userId);
    }

    public void ResetSession(string userId, Guid quizId, string? practiceDirection, string? practiceItemType, int wordCount)
    {
        var active = _registry.FindActive(
            userId,
            Mode,
            quizId,
            PracticeDirection.Normalize(practiceDirection),
            PracticeItemType.Normalize(practiceItemType),
            Math.Clamp(wordCount, 1, 100));

        if (active != null)
            _registry.Deregister(userId, active.SessionId, removeSessionData: true);
    }

    private static bool IsComplete(TypingSessionData session)
        => session.CurrentIndex >= session.Words.Count;

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
