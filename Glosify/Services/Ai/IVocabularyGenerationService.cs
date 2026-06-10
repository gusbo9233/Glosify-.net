namespace Glosify.Services;

public interface IVocabularyGenerationService
{
    Task<RepairWordResult?> RepairWordAsync(
        RepairQuizData quizData,
        string wordId,
        CancellationToken cancellationToken = default);

    Task<RepairSentenceResult?> RepairSentenceAsync(
        RepairQuizData quizData,
        string sentenceText,
        CancellationToken cancellationToken = default);
}
