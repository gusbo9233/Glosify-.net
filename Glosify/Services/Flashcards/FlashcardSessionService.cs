using Microsoft.Extensions.Caching.Memory;

namespace Glosify.Services;

public class FlashcardSessionService : IFlashcardSessionService
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(45);
    private readonly IMemoryCache _cache;

    public FlashcardSessionService(IMemoryCache cache)
    {
        _cache = cache;
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
    }

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
