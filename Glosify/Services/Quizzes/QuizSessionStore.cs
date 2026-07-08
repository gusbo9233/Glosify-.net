using Glosify.Models;
using Microsoft.Extensions.Caching.Memory;

namespace Glosify.Services.Quizzes;

/// <summary>
/// The session-shape members the shared store needs; implemented by the
/// flashcard and typing session records.
/// </summary>
public interface IQuizSessionData
{
    string SessionId { get; }
    string UserId { get; }
    Guid QuizId { get; }
    string PracticeDirection { get; }
    string PracticeItemType { get; }
    int WordCount { get; }
    int WordRangeStart { get; }
    int WordRangeEnd { get; }
}

/// <summary>
/// Shared cache and registry plumbing for in-progress practice sessions: store,
/// look up, resume, and reset. Mode-specific behavior (starting a session, rating
/// or answering) lives in the derived services.
/// </summary>
public abstract class QuizSessionStore<TSession> where TSession : class, IQuizSessionData
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(45);

    private readonly IMemoryCache _cache;
    private readonly IQuizSessionRegistry _registry;

    protected QuizSessionStore(IMemoryCache cache, IQuizSessionRegistry registry)
    {
        _cache = cache;
        _registry = registry;
    }

    /// <summary>Registry mode discriminator (e.g. "flashcards").</summary>
    protected abstract string Mode { get; }

    /// <summary>Cache key prefix; distinct per mode so keys never collide.</summary>
    protected abstract string CacheKeyPrefix { get; }

    protected abstract bool IsComplete(TSession session);

    public TSession? FindSession(string sessionId, string userId)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(sessionId))
            return null;

        return _cache.TryGetValue(CacheKey(sessionId), out TSession? session)
            && session?.UserId == userId
            ? session
            : null;
    }

    public void SaveSession(TSession session)
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
                WordRangeStart = session.WordRangeStart,
                WordRangeEnd = session.WordRangeEnd,
                CacheKey = CacheKey(session.SessionId)
            });
        }
    }

    public TSession? FindResumableSession(string userId, Guid quizId, string? practiceDirection, string? practiceItemType, int wordCount, int rangeStartPercent = 0, int rangeEndPercent = 100)
    {
        var active = FindActive(userId, quizId, practiceDirection, practiceItemType, wordCount, rangeStartPercent, rangeEndPercent);
        return active == null ? null : FindSession(active.SessionId, userId);
    }

    public void ResetSession(string userId, Guid quizId, string? practiceDirection, string? practiceItemType, int wordCount, int rangeStartPercent = 0, int rangeEndPercent = 100)
    {
        var active = FindActive(userId, quizId, practiceDirection, practiceItemType, wordCount, rangeStartPercent, rangeEndPercent);
        if (active != null)
            _registry.Deregister(userId, active.SessionId, removeSessionData: true);
    }

    private ActiveQuizSession? FindActive(string userId, Guid quizId, string? practiceDirection, string? practiceItemType, int wordCount, int rangeStartPercent, int rangeEndPercent)
        => _registry.FindActive(
            userId,
            Mode,
            quizId,
            PracticeDirection.Normalize(practiceDirection),
            PracticeItemType.Normalize(practiceItemType),
            Math.Clamp(wordCount, 1, 100),
            Math.Clamp(rangeStartPercent, 0, 100),
            Math.Clamp(rangeEndPercent, 0, 100));

    private string CacheKey(string sessionId) => $"{CacheKeyPrefix}:{sessionId}";
}
