using System.Collections.Concurrent;
using System.Reflection;
using Glosify.Services;
using Glosify.Services.Flashcards;
using Glosify.Services.Quizzes;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Glosify.Tests;

public class QuizSessionRegistryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingAnOldUserEntry_DoesNotLoseAReplacementSession(bool findActive)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var registry = new QuizSessionRegistry(cache);
        var service = new FlashcardSessionService(cache, registry);
        var oldSession = StartFlashcardSession(service, "user-1", Guid.NewGuid(), cardCount: 2);
        service.SaveSession(oldSession);
        var replacement = StartFlashcardSession(service, "user-1", Guid.NewGuid(), cardCount: 2);

        // Hold the original list so the competing request has fetched that entry
        // but cannot inspect it until it has been removed and replaced.
        var entries = (ConcurrentDictionary<string, List<ActiveQuizSession>>)typeof(QuizSessionRegistry)
            .GetField("_sessionsByUser", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry)!;
        Exception? failure = null;
        var request = new Thread(() =>
        {
            try
            {
                if (findActive)
                    service.FindResumableSession("user-1", oldSession.QuizId,
                        PracticeDirection.SourceToTarget, PracticeItemType.Words, 2);
                else
                    registry.Deregister("user-1", oldSession.SessionId);
            }
            catch (Exception exception) { failure = exception; }
        });
        bool waiting;
        lock (entries["user-1"])
        {
            request.Start();
            waiting = SpinWait.SpinUntil(() => (request.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(5));
            registry.Deregister("user-1", oldSession.SessionId);
            service.SaveSession(replacement);
        }

        Assert.True(request.Join(TimeSpan.FromSeconds(5)), "The competing request did not finish.");
        Assert.True(waiting, "The competing request did not reach the original list lock.");
        Assert.Null(failure);
        var resumed = service.FindResumableSession("user-1", replacement.QuizId,
            PracticeDirection.SourceToTarget, PracticeItemType.Words, 2);
        Assert.Equal(replacement.SessionId, resumed?.SessionId);
    }

    [Fact]
    public void SaveSession_MakesSessionResumableWithMatchingSettings()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new FlashcardSessionService(cache, new QuizSessionRegistry(cache));
        var quizId = Guid.NewGuid();
        var session = StartFlashcardSession(service, "user-1", quizId, cardCount: 2);
        service.SaveSession(session);

        var resumed = service.FindResumableSession("user-1", quizId, PracticeDirection.SourceToTarget, PracticeItemType.Words, 2);

        Assert.NotNull(resumed);
        Assert.Equal(session.SessionId, resumed!.SessionId);
    }

    [Fact]
    public void FindResumableSession_DoesNotMatchDifferentSettingsOrUser()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new FlashcardSessionService(cache, new QuizSessionRegistry(cache));
        var quizId = Guid.NewGuid();
        var session = StartFlashcardSession(service, "user-1", quizId, cardCount: 2);
        service.SaveSession(session);

        Assert.Null(service.FindResumableSession("user-2", quizId, PracticeDirection.SourceToTarget, PracticeItemType.Words, 2));
        Assert.Null(service.FindResumableSession("user-1", Guid.NewGuid(), PracticeDirection.SourceToTarget, PracticeItemType.Words, 2));
        Assert.Null(service.FindResumableSession("user-1", quizId, PracticeDirection.TargetToSource, PracticeItemType.Words, 2));
        Assert.Null(service.FindResumableSession("user-1", quizId, PracticeDirection.SourceToTarget, PracticeItemType.Sentences, 2));
        Assert.Null(service.FindResumableSession("user-1", quizId, PracticeDirection.SourceToTarget, PracticeItemType.Words, 5));
    }

    [Fact]
    public void CompletedSession_IsNoLongerResumable()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new FlashcardSessionService(cache, new QuizSessionRegistry(cache));
        var quizId = Guid.NewGuid();
        var session = StartFlashcardSession(service, "user-1", quizId, cardCount: 1);
        service.SaveSession(session);

        service.ApplyRating(session, "good");
        service.SaveSession(session);

        Assert.Null(service.FindResumableSession("user-1", quizId, PracticeDirection.SourceToTarget, PracticeItemType.Words, 1));
        // The session data itself is kept so the completion screen (e.g. Practice Again) still works.
        Assert.NotNull(service.FindSession(session.SessionId, "user-1"));
    }

    [Fact]
    public void ResetSession_RemovesSessionAndItsData()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new FlashcardSessionService(cache, new QuizSessionRegistry(cache));
        var quizId = Guid.NewGuid();
        var session = StartFlashcardSession(service, "user-1", quizId, cardCount: 2);
        service.SaveSession(session);

        service.ResetSession("user-1", quizId, PracticeDirection.SourceToTarget, PracticeItemType.Words, 2);

        Assert.Null(service.FindResumableSession("user-1", quizId, PracticeDirection.SourceToTarget, PracticeItemType.Words, 2));
        Assert.Null(service.FindSession(session.SessionId, "user-1"));
    }

    [Fact]
    public void Register_EvictsOldestSessionBeyondCap()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new FlashcardSessionService(cache, new QuizSessionRegistry(cache));
        var quizIds = Enumerable.Range(0, QuizSessionRegistry.MaxActiveSessionsPerUser + 1)
            .Select(_ => Guid.NewGuid())
            .ToList();

        var sessions = quizIds
            .Select(quizId => StartFlashcardSession(service, "user-1", quizId, cardCount: 2))
            .ToList();
        foreach (var session in sessions)
            service.SaveSession(session);

        // The oldest session was evicted, including its cached data.
        Assert.Null(service.FindResumableSession("user-1", quizIds[0], PracticeDirection.SourceToTarget, PracticeItemType.Words, 2));
        Assert.Null(service.FindSession(sessions[0].SessionId, "user-1"));

        // The remaining five are still active.
        for (var i = 1; i < sessions.Count; i++)
        {
            var resumed = service.FindResumableSession("user-1", quizIds[i], PracticeDirection.SourceToTarget, PracticeItemType.Words, 2);
            Assert.Equal(sessions[i].SessionId, resumed?.SessionId);
        }
    }

    [Fact]
    public void SessionCap_IsPerUser()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new FlashcardSessionService(cache, new QuizSessionRegistry(cache));

        var firstUserQuizId = Guid.NewGuid();
        var firstUserSession = StartFlashcardSession(service, "user-1", firstUserQuizId, cardCount: 2);
        service.SaveSession(firstUserSession);

        for (var i = 0; i < QuizSessionRegistry.MaxActiveSessionsPerUser; i++)
            service.SaveSession(StartFlashcardSession(service, "user-2", Guid.NewGuid(), cardCount: 2));

        Assert.NotNull(service.FindResumableSession("user-1", firstUserQuizId, PracticeDirection.SourceToTarget, PracticeItemType.Words, 2));
    }

    private static FlashcardSessionData StartFlashcardSession(FlashcardSessionService service, string userId, Guid quizId, int cardCount)
    {
        var cards = Enumerable.Range(0, cardCount)
            .Select(i => new FlashcardCardData { Id = $"{i}", Lemma = $"lemma-{i}", Translation = $"translation-{i}" })
            .ToList();

        return service.StartSession(
            userId,
            quizId,
            "Spanish basics",
            "English",
            "Spanish",
            cardCount,
            cards,
            PracticeDirection.SourceToTarget,
            PracticeItemType.Words);
    }
}
