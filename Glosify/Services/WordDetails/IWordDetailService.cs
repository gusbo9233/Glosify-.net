using Glosify.Models;

namespace Glosify.Services;

public interface IWordDetailService
{
    Task<IReadOnlyList<WordDetail>> ListForUserAsync(string userId);
    Task<OwnedWordDetail?> LoadOwnedAsync(string id, string userId);
    Task<OwnedWordDetailWithWord?> LoadOwnedWithWordAsync(string id, string userId);
    Task<OwnedWord?> LoadOwnedWordAsync(string id, string userId, CancellationToken cancellationToken = default);
    Task<WordDetail?> FindCachedAsync(string sourceLanguage, string targetLanguage, string word, string translation, CancellationToken cancellationToken = default);
    Task<WordDetail> GetOrCreateAndLinkAsync(Word word, Quiz quiz, string wordDetailWord, CancellationToken cancellationToken = default);
    Task<bool> HasReferencesAsync(string id);
    Task<WordDetail?> CreateAsync(CreateWordDetailInput input);
    Task<bool> UpdateAsync(EditWordDetailInput input, string userId);
    Task<bool> DeleteAsync(string id, string userId);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

public sealed record OwnedWordDetail(WordDetail Detail, Quiz Quiz);
public sealed record OwnedWordDetailWithWord(WordDetail Detail, Word Word, Quiz Quiz);
public sealed record OwnedWord(Word Word, Quiz Quiz);
