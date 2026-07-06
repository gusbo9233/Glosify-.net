using Microsoft.Extensions.Caching.Memory;
using Glosify.Services.Quizzes;

namespace Glosify.Services.Flashcards;

public class FlashcardSessionService : IFlashcardSessionService
{
    private const string Mode = "flashcards";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(45);
    private readonly IMemoryCache _cache;
    private readonly IQuizSessionRegistry _registry;

    public FlashcardSessionService(IMemoryCache cache, IQuizSessionRegistry registry)
    {
        _cache = cache;
        _registry = registry;
    }

    public FlashcardSessionData StartSession(
        string userId,
        Guid quizId,
        string quizName,
        string sourceLanguage,
        string targetLanguage,
        int wordCount,
        IReadOnlyList<FlashcardCardData> cards,
        string? practiceDirection = null,
        string? practiceItemType = null)
    {
        var normalizedDirection = PracticeDirection.Normalize(practiceDirection);
        var normalizedItemType = PracticeItemType.Normalize(practiceItemType);
        return new FlashcardSessionData
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
            Cards = cards.Select(card => card with
            {
                Prompt = PracticeDirection.IsSourceToTarget(normalizedDirection) ? card.Translation : card.Lemma,
                Answer = PracticeDirection.IsSourceToTarget(normalizedDirection) ? card.Lemma : card.Translation
            }).ToList()
        };
    }

    public FlashcardSessionData? FindSession(string sessionId, string userId)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(sessionId))
            return null;

        return _cache.TryGetValue(CacheKey(sessionId), out FlashcardSessionData? session)
            && session?.UserId == userId
            ? session
            : null;
    }

    public void SaveSession(FlashcardSessionData session)
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

    public FlashcardSessionData? FindResumableSession(string userId, Guid quizId, string? practiceDirection, string? practiceItemType, int wordCount)
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

    private static bool IsComplete(FlashcardSessionData session)
        => session.CurrentIndex >= session.Cards.Count;

    public void ApplyRating(FlashcardSessionData session, string rating)
    {
        if (session.CurrentIndex >= session.Cards.Count)
            return;

        var currentCard = session.Cards[session.CurrentIndex];

        switch (rating?.Trim().ToLowerInvariant())
        {
            case "again":
                session.AgainCount++;
                session.AgainCards.Add(currentCard);
                break;
            case "skip":
                session.SkippedCount++;
                break;
            default:
                session.RememberedCount++;
                break;
        }

        session.CurrentIndex++;
        session.IsAnswerRevealed = false;
    }

    public void RevealAnswer(FlashcardSessionData session)
    {
        session.IsAnswerRevealed = true;
    }

    private static string CacheKey(string sessionId) => $"flashcard-quiz:{sessionId}";

    public FlashcardSessionData RestartWithAgainCards(FlashcardSessionData session)
    {
       return StartSession(
       session.UserId,
       session.QuizId,
       session.QuizName,
       session.SourceLanguage,
       session.TargetLanguage,
       session.AgainCards.Count,
       session.AgainCards,
       session.PracticeDirection,
       session.PracticeItemType);
    }
}
