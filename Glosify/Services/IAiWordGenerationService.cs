using Glosify.Models;

namespace Glosify.Services;

public interface IAiWordGenerationService
{
    Task<IReadOnlyDictionary<string, GeneratedWord>> GenerateWordsFromTextAsync(
        string input,
        string knownLanguage,
        string targetLanguage);

    Task<IReadOnlyDictionary<string, GeneratedWord>> GenerateWordsFromTextAsync(
        string input,
        string knownLanguage,
        string targetLanguage,
        IReadOnlyList<string> sourceSentences);

    Task<GeneratedWordDetail?> GenerateWordDetailAsync(
        string word,
        string translation,
        string knownLanguage,
        string targetLanguage);

    bool ValidateResponse(string json);
}
