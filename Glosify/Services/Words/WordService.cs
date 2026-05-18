using Glosify.Data;
using Glosify.Models;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services;

public class WordService : IWordService
{
    private readonly GlosifyContext _context;

    public WordService(GlosifyContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<Word>> GetWordsAsync(Guid quizId)
    {
        return await _context.Words
            .Where(w => w.QuizId == quizId)
            .OrderBy(w => w.Lemma)
            .ToListAsync();
    }

    public async Task<IReadOnlySet<string>> GetEnrichedWordDetailIdsAsync(Guid quizId)
    {
        var ids = await _context.Words
            .Where(word => word.QuizId == quizId)
            .Join(
                _context.WordDetails,
                word => word.WordDetailId,
                detail => detail.Id,
                (_, detail) => detail)
            .Where(detail =>
                detail.Properties != "{}"
                && detail.Variants != "[]"
                && detail.Explanation != null
                && detail.Explanation != ""
                && detail.ExampleSentence != null
                && detail.ExampleSentence != "")
            .Select(detail => detail.Id)
            .ToListAsync();

        return new HashSet<string>(ids, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<WordDetail>> GetWordDetailsAsync(Guid quizId)
    {
        return await _context.Words
            .Where(word => word.QuizId == quizId)
            .Join(
                _context.WordDetails,
                word => word.WordDetailId,
                detail => detail.Id,
                (_, detail) => detail)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<QuizCardData>> LoadCardsAsync(Guid quizId, int wordCount)
    {
        var take = Math.Clamp(wordCount, 1, 100);

        var cards = await _context.Words
            .Where(word => word.QuizId == quizId)
            .GroupJoin(
                _context.WordDetails,
                word => word.WordDetailId,
                detail => detail.Id,
                (word, details) => new { Word = word, Detail = details.FirstOrDefault() })
            .OrderBy(_ => Guid.NewGuid())
            .Take(take)
            .ToListAsync();

        return cards
            .Select(item => new QuizCardData
            {
                Id = item.Word.Id,
                Lemma = item.Word.Lemma,
                Translation = item.Word.Translation,
                ExampleSentence = item.Detail == null ? string.Empty : CleanExampleForDisplay(item.Detail.ExampleSentence),
                ExampleTranslation = item.Detail == null ? string.Empty : item.Detail.ExampleSentenceTranslation
            })
            .ToList();
    }

    public async Task<IReadOnlyList<QuizSentenceData>> GetSentencesAsync(Guid quizId)
    {
        var wordDetails = await _context.Words
            .Where(word => word.QuizId == quizId)
            .Join(
                _context.WordDetails,
                word => word.WordDetailId,
                detail => detail.Id,
                (_, detail) => detail)
            .ToListAsync();

        return wordDetails
            .Where(detail => !string.IsNullOrWhiteSpace(detail.ExampleSentence))
            .Select(detail => new
            {
                ExampleSentence = CleanExampleForDisplay(detail.ExampleSentence),
                detail.ExampleSentenceTranslation
            })
            .Where(detail => !string.IsNullOrWhiteSpace(detail.ExampleSentence))
            .GroupBy(detail => detail.ExampleSentence, StringComparer.OrdinalIgnoreCase)
            .Select(group => new QuizSentenceData
            {
                Text = group.Key,
                Translation = group
                    .Select(detail => detail.ExampleSentenceTranslation.Trim())
                    .FirstOrDefault(translation => !string.IsNullOrWhiteSpace(translation)) ?? string.Empty,
                WordCount = group.Count()
            })
            .OrderBy(sentence => sentence.Text)
            .ToList();
    }

    private static string CleanExampleForDisplay(string? exampleSentence)
    {
        return string.IsNullOrWhiteSpace(exampleSentence)
            ? string.Empty
            : VocabularyInputCleaner.CleanForVocabulary(exampleSentence).Trim();
    }

    public async Task<bool> AddWordAsync(Guid quizId, string word, string translation, string sourceLanguage, string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(word) || string.IsNullOrWhiteSpace(translation))
            return false;

        var lemma = word.Trim();
        var cleanTranslation = translation.Trim();
        var wordDetail = await GetOrCreateWordDetailAsync(sourceLanguage, targetLanguage, lemma, cleanTranslation);

        _context.Words.Add(new Word
        {
            Id = Guid.NewGuid().ToString("N"),
            QuizId = quizId,
            Lemma = lemma,
            Translation = cleanTranslation,
            WordDetailId = wordDetail.Id
        });

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<Word?> DeleteWordAsync(string wordId, string userId)
    {
        if (string.IsNullOrWhiteSpace(wordId))
            return null;

        var word = await _context.Words.FirstOrDefaultAsync(w => w.Id == wordId);
        if (word == null)
            return null;

        var ownsQuiz = await _context.Quizzes
            .AnyAsync(q => q.Id == word.QuizId && q.UserId == userId);

        if (!ownsQuiz)
            return null;

        _context.Words.Remove(word);
        await _context.SaveChangesAsync();

        return word;
    }

    public async Task<bool> WordExistsAsync(Guid quizId, string lemma)
    {
        return await _context.Words
            .AnyAsync(w => w.QuizId == quizId && w.Lemma == lemma);
    }

    private async Task<WordDetail> GetOrCreateWordDetailAsync(
        string sourceLanguage,
        string targetLanguage,
        string word,
        string translation)
    {
        var key = WordDetailKey.Create(sourceLanguage, targetLanguage, word, translation);
        var existing = await _context.WordDetails.FirstOrDefaultAsync(detail => detail.Id == key.Id);
        if (existing != null)
        {
            return existing;
        }

        var now = DateTimeOffset.UtcNow;
        var detail = new WordDetail
        {
            Id = key.Id,
            SourceLanguage = key.SourceLanguage,
            TargetLanguage = key.TargetLanguage,
            Word = key.Word,
            Translation = key.Translation,
            NormalizedWord = key.NormalizedWord,
            NormalizedTranslation = key.NormalizedTranslation,
            NormalizedWordHash = key.NormalizedWordHash,
            NormalizedTranslationHash = key.NormalizedTranslationHash,
            Language = key.TargetLanguage,
            Properties = "{}",
            Variants = "[]",
            Explanation = string.Empty,
            ExampleSentence = string.Empty,
            ExampleSentenceTranslation = string.Empty,
            CreatedAt = now,
            UpdatedAt = now
        };

        _context.WordDetails.Add(detail);
        return detail;
    }
}
