using Glosify.Services.Quizzes;
using Microsoft.Extensions.Caching.Memory;

namespace Glosify.Services.Flashcards;

public class FlashcardSessionService : QuizSessionStore<FlashcardSessionData>, IFlashcardSessionService
{
    public FlashcardSessionService(IMemoryCache cache, IQuizSessionRegistry registry)
        : base(cache, registry)
    {
    }

    protected override string Mode => "flashcards";
    protected override string CacheKeyPrefix => "flashcard-quiz";

    protected override bool IsComplete(FlashcardSessionData session)
        => session.CurrentIndex >= session.Cards.Count;

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
