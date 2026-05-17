namespace Glosify.Services;

public interface IQuizServerVocabularyGenerationService
{
    Task<IReadOnlyDictionary<string, GeneratedWord>> GenerateWordsFromTextAsync(
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

    Task<QuizServerRepairQuizResult?> RepairQuizAsync(
        QuizServerRepairQuizData quizData,
        CancellationToken cancellationToken = default);

    Task<QuizServerRepairWordResult?> RepairWordAsync(
        QuizServerRepairQuizData quizData,
        string wordId,
        CancellationToken cancellationToken = default);

    Task<QuizServerRepairSentenceResult?> RepairSentenceAsync(
        QuizServerRepairQuizData quizData,
        string sentenceText,
        CancellationToken cancellationToken = default);
}
