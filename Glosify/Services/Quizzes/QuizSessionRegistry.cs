using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace Glosify.Services.Quizzes;

public record ActiveQuizSession
{
    public string UserId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public Guid QuizId { get; init; }
    public string PracticeDirection { get; init; } = string.Empty;
    public string PracticeItemType { get; init; } = string.Empty;
    public int WordCount { get; init; }
    public string CacheKey { get; init; } = string.Empty;
    public DateTimeOffset StartedAt { get; init; }
}

public interface IQuizSessionRegistry
{
    void Register(ActiveQuizSession session);
    ActiveQuizSession? FindActive(string userId, string mode, Guid quizId, string practiceDirection, string practiceItemType, int wordCount);
    void Deregister(string userId, string sessionId, bool removeSessionData = false);
}

/// <summary>
/// Tracks in-progress quiz sessions per user so navigation (e.g. the back button)
/// resumes an existing session instead of starting a new one. Capped at
/// <see cref="MaxActiveSessionsPerUser"/> active sessions per user; the oldest
/// session is evicted when the cap is exceeded.
/// </summary>
public class QuizSessionRegistry : IQuizSessionRegistry
{
    public const int MaxActiveSessionsPerUser = 5;

    private readonly ConcurrentDictionary<string, List<ActiveQuizSession>> _sessionsByUser = new();
    private readonly IMemoryCache _cache;

    public QuizSessionRegistry(IMemoryCache cache)
    {
        _cache = cache;
    }

    public void Register(ActiveQuizSession session)
    {
        if (string.IsNullOrWhiteSpace(session.UserId) || string.IsNullOrWhiteSpace(session.SessionId))
            return;

        while (true)
        {
            var sessions = _sessionsByUser.GetOrAdd(session.UserId, _ => []);
            lock (sessions)
            {
                // Another thread may have evicted this (emptied) entry between the
                // GetOrAdd and the lock; adding to the orphaned list would lose the
                // session, so retry with a fresh entry.
                if (!_sessionsByUser.TryGetValue(session.UserId, out var current)
                    || !ReferenceEquals(current, sessions))
                {
                    continue;
                }

                PruneExpired(sessions);
                if (sessions.Any(s => s.SessionId == session.SessionId))
                    return;

                sessions.Add(session with { StartedAt = DateTimeOffset.UtcNow });
                while (sessions.Count > MaxActiveSessionsPerUser)
                {
                    var oldest = sessions.MinBy(s => s.StartedAt)!;
                    sessions.Remove(oldest);
                    _cache.Remove(oldest.CacheKey);
                }

                return;
            }
        }
    }

    public ActiveQuizSession? FindActive(string userId, string mode, Guid quizId, string practiceDirection, string practiceItemType, int wordCount)
    {
        if (string.IsNullOrWhiteSpace(userId) || !_sessionsByUser.TryGetValue(userId, out var sessions))
            return null;

        lock (sessions)
        {
            PruneExpired(sessions);
            var active = sessions.FirstOrDefault(s =>
                s.Mode == mode
                && s.QuizId == quizId
                && string.Equals(s.PracticeDirection, practiceDirection, StringComparison.OrdinalIgnoreCase)
                && string.Equals(s.PracticeItemType, practiceItemType, StringComparison.OrdinalIgnoreCase)
                && s.WordCount == wordCount);
            RemoveUserEntryIfEmpty(userId, sessions);
            return active;
        }
    }

    public void Deregister(string userId, string sessionId, bool removeSessionData = false)
    {
        if (string.IsNullOrWhiteSpace(userId) || !_sessionsByUser.TryGetValue(userId, out var sessions))
            return;

        lock (sessions)
        {
            var session = sessions.FirstOrDefault(s => s.SessionId == sessionId);
            if (session != null)
            {
                sessions.Remove(session);
                if (removeSessionData)
                    _cache.Remove(session.CacheKey);
            }

            RemoveUserEntryIfEmpty(userId, sessions);
        }
    }

    private void PruneExpired(List<ActiveQuizSession> sessions)
    {
        sessions.RemoveAll(s => !_cache.TryGetValue(s.CacheKey, out _));
    }

    // Must be called while holding the lock on the user's session list; Register
    // re-checks entry identity after locking, so a racing Register retries instead
    // of adding to the removed list.
    private void RemoveUserEntryIfEmpty(string userId, List<ActiveQuizSession> sessions)
    {
        if (sessions.Count == 0)
            _sessionsByUser.TryRemove(userId, out _);
    }
}
