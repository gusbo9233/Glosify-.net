using Glosify.Services.Flashcards;
using Glosify.Services.Typing;

namespace Glosify.Services.Quizzes;

public interface IQuizAttemptService
{
    Task RecordFlashcardAttemptAsync(FlashcardSessionData session, CancellationToken cancellationToken = default);
    Task RecordTypingAttemptAsync(TypingSessionData session, CancellationToken cancellationToken = default);
}
