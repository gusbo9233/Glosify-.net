using Glosify.Models;

namespace Glosify.Services;

public interface IDictionaryService
{
    Task<DictionaryEntry?> FindManualDictionaryMatchAsync(string language, string word);
    Task<DictionaryEntry?> FindBestDictionaryMatchAsync(string language, string word, string? selectedPartOfSpeech);
    Task<Dictionary<string, DictionaryEntry?>> BatchFindDictionaryMatchesAsync(string language, IReadOnlyList<string> lemmas);
}
