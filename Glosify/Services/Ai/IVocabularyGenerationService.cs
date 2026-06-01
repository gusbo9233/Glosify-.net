namespace Glosify.Services;

public interface IVocabularyGenerationService
{
    Task<GeneratedVocabularyBatch> GenerateWordsFromTextAsync(
        string input,
        string sourceLanguage,
        string targetLanguage,
        string? quizName,
        CancellationToken cancellationToken = default);

    Task<GeneratedWordDetail?> GenerateWordDetailAsync(
        string word,
        string translation,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default);

    Task<RepairQuizResult?> RepairQuizAsync(
        RepairQuizData quizData,
        CancellationToken cancellationToken = default);

    Task<RepairWordResult?> RepairWordAsync(
        RepairQuizData quizData,
        string wordId,
        CancellationToken cancellationToken = default);

    Task<RepairSentenceResult?> RepairSentenceAsync(
        RepairQuizData quizData,
        string sentenceText,
        CancellationToken cancellationToken = default);
}
