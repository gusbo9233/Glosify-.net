namespace Glosify.Services.Quizzes;

public interface IQuizRepairService
{
    Task<QuizRepairResult> RepairWordAsync(string wordId, string userId, CancellationToken cancellationToken);
    Task<QuizRepairResult> RepairSentenceAsync(Guid quizId, string sentenceText, string userId, CancellationToken cancellationToken);
}

public enum QuizRepairStatus
{
    NotFound,
    LlmUnavailable,
    Success
}

public sealed record QuizRepairResult(
    QuizRepairStatus Status,
    int UpdatedCount = 0,
    string? Word = null);
