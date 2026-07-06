using Glosify.Models;

namespace Glosify.Services.Words;

public interface IWordService
{
    Task<IReadOnlyList<Word>> GetWordsAsync(Guid quizId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QuizCardData>> LoadCardsAsync(Guid quizId, int wordCount, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QuizCardData>> LoadSentenceCardsAsync(Guid quizId, int sentenceCount, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QuizSentenceData>> GetSentencesAsync(Guid quizId, CancellationToken cancellationToken = default);
    Task<bool> AddWordAsync(Guid quizId, string word, string translation, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default);
    Task<Word?> DeleteWordAsync(string wordId, string userId, CancellationToken cancellationToken = default);
    Task<bool> WordExistsAsync(Guid quizId, string word, CancellationToken cancellationToken = default);
}

public sealed record QuizCardData
{
    public string Id { get; init; } = string.Empty;
    public string Lemma { get; init; } = string.Empty;
    public string Translation { get; init; } = string.Empty;
    public string Prompt { get; init; } = string.Empty;
    public string Answer { get; init; } = string.Empty;
    public string ExampleSentence { get; init; } = string.Empty;
    public string ExampleTranslation { get; init; } = string.Empty;
}

public sealed record QuizSentenceData
{
    public Guid Id { get; init; }
    public string Text { get; init; } = string.Empty;
    public string Translation { get; init; } = string.Empty;
    public int WordCount { get; init; }
}
