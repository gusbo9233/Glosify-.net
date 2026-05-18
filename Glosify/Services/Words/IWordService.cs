using Glosify.Models;

namespace Glosify.Services;

public interface IWordService
{
    Task<IReadOnlyList<Word>> GetWordsAsync(Guid quizId);
    Task<IReadOnlySet<string>> GetEnrichedWordDetailIdsAsync(Guid quizId);
    Task<IReadOnlyList<WordDetail>> GetWordDetailsAsync(Guid quizId);
    Task<IReadOnlyList<QuizCardData>> LoadCardsAsync(Guid quizId, int wordCount);
    Task<IReadOnlyList<QuizSentenceData>> GetSentencesAsync(Guid quizId);
    Task<bool> AddWordAsync(Guid quizId, string word, string translation, string sourceLanguage, string targetLanguage);
    Task<Word?> DeleteWordAsync(string wordId, string userId);
    Task<bool> WordExistsAsync(Guid quizId, string lemma);
}

public sealed record QuizCardData
{
    public string Id { get; init; } = string.Empty;
    public string Lemma { get; init; } = string.Empty;
    public string Translation { get; init; } = string.Empty;
    public string ExampleSentence { get; init; } = string.Empty;
    public string ExampleTranslation { get; init; } = string.Empty;
}

public sealed record QuizSentenceData
{
    public string Text { get; init; } = string.Empty;
    public string Translation { get; init; } = string.Empty;
    public int WordCount { get; init; }
}
