namespace Glosify.Services;

public interface IQuizServerVocabularyGenerationService
{
    Task<IReadOnlyDictionary<string, GeneratedWord>> GenerateWordsFromTextAsync(
        string input,
        string sourceLanguage,
        string targetLanguage,
        string? quizName,
        CancellationToken cancellationToken = default);
}
