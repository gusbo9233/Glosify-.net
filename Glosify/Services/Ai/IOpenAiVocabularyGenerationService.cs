namespace Glosify.Services;

public interface IOpenAiVocabularyGenerationService
{
    Task<IReadOnlyDictionary<string, GeneratedWord>> GenerateWordsFromTextAsync(
        string input,
        string knownLanguage,
        string targetLanguage,
        IReadOnlyList<string> sourceSentences,
        CancellationToken cancellationToken = default);
}
