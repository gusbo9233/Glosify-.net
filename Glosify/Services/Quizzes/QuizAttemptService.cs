using Glosify.Data;
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
        // Skips are not attributable to individual cards, so flashcard attempts
        // store summary counts only (no item rows).
        var attempt = new QuizAttempt
        {
            Id = Guid.NewGuid(),
            QuizId = session.QuizId,
            UserId = session.UserId,
            ClassroomId = session.ClassroomId,
            Mode = "flashcards",
            PracticeDirection = session.PracticeDirection,
            PracticeItemType = session.PracticeItemType,
            TotalItems = session.Cards.Count,
            CorrectCount = session.RememberedCount,
            IncorrectCount = session.AgainCount,
            SkippedCount = session.SkippedCount,
            StartedAt = session.StartedAt,
            CompletedAt = DateTimeOffset.UtcNow
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
            ClassroomId = session.ClassroomId,
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
