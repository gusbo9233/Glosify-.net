using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Flashcards;
using Glosify.Services.Typing;

namespace Glosify.Services.Quizzes;

public class QuizAttemptService : IQuizAttemptService
{
    private readonly GlosifyContext _context;

    public QuizAttemptService(GlosifyContext context)
    {
        _context = context;
    }

    public async Task RecordFlashcardAttemptAsync(FlashcardSessionData session, CancellationToken cancellationToken = default)
    {
        // Old/in-flight sessions without rating records keep summary-only history.
        // Never infer individual correctness from aggregate counts.
        var attempt = new QuizAttempt
        {
            Id = Guid.NewGuid(),
            QuizId = session.QuizId,
            UserId = session.UserId,
            Mode = "flashcards",
            PracticeDirection = session.PracticeDirection,
            PracticeItemType = session.PracticeItemType,
            TotalItems = session.Cards.Count,
            CorrectCount = session.RememberedCount,
            IncorrectCount = session.AgainCount,
            SkippedCount = session.SkippedCount,
            StartedAt = session.StartedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            Items = session.Ratings.Where(r => r.Sequence >= 0 && r.Sequence < session.Cards.Count)
                .GroupBy(r => r.Sequence).Select(group => group.First()).Select(r => new QuizAttemptItem
                {
                    Id = Guid.NewGuid(),
                    ItemId = session.Cards[r.Sequence].Id,
                    Prompt = Truncate(session.Cards[r.Sequence].Prompt),
                    ExpectedAnswer = Truncate(session.Cards[r.Sequence].Answer),
                    IsCorrect = r.Rating == "remembered",
                    IsSkipped = r.Rating == "skip",
                    Sequence = r.Sequence,
                }).ToList()
        };

        _context.QuizAttempts.Add(attempt);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordTypingAttemptAsync(TypingSessionData session, CancellationToken cancellationToken = default)
    {
        var incorrectIds = session.IncorrectWords.Select(word => word.Id).ToHashSet();

        var attempt = new QuizAttempt
        {
            Id = Guid.NewGuid(),
            QuizId = session.QuizId,
            UserId = session.UserId,
            Mode = "typing",
            PracticeDirection = session.PracticeDirection,
            PracticeItemType = session.PracticeItemType,
            TotalItems = session.Words.Count,
            CorrectCount = session.CorrectCount,
            IncorrectCount = session.IncorrectCount,
            SkippedCount = 0,
            StartedAt = session.StartedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            Items = session.Words.Select((word, index) => new QuizAttemptItem
            {
                Id = Guid.NewGuid(),
                ItemId = word.Id,
                Prompt = Truncate(word.Prompt),
                ExpectedAnswer = Truncate(word.Answer),
                IsCorrect = !incorrectIds.Contains(word.Id),
                Sequence = index
            }).ToList()
        };

        _context.QuizAttempts.Add(attempt);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private static string Truncate(string value)
        => value.Length <= 512 ? value : value[..512];
}
