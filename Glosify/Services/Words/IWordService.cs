using Glosify.Models;

namespace Glosify.Services;

public interface IWordService
{
    Task<IReadOnlyList<Word>> GetWordsAsync(Guid quizId);
    Task<IReadOnlySet<string>> GetEnrichedWordDetailIdsAsync(Guid quizId);
    Task<IReadOnlyList<WordDetail>> GetWordDetailsAsync(Guid quizId);
    Task<IReadOnlyList<TypingQuizWordViewModel>> LoadWordsAsync(Guid quizId, int wordCount);
    Task<IReadOnlyList<FlashcardWordViewModel>> LoadCardsAsync(Guid quizId, int wordCount);
    Task<IReadOnlyList<QuizSentenceViewModel>> GetSentencesAsync(Guid quizId);
    Task<bool> AddWordAsync(Guid quizId, string word, string translation, string sourceLanguage, string targetLanguage);
    Task<bool> DeleteWordAsync(string wordId, string userId);
    Task<bool> WordExistsAsync(Guid quizId, string lemma);
}
