namespace Glosify.Services;

public interface IVocabularyGenerationService
{
    Task<RepairWordResult?> RepairWordAsync(
        RepairQuizData quizData,
        string wordId,
        string userId,
        CancellationToken cancellationToken = default);

    Task<RepairSentenceResult?> RepairSentenceAsync(
        RepairQuizData quizData,
        string sentenceText,
        string userId,
        CancellationToken cancellationToken = default);
}
