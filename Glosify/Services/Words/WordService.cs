using Glosify.Data;
using Glosify.Models;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services;

public class WordService : IWordService
{
    private readonly GlosifyContext _context;
    private readonly IAiEnrichmentQueue _enrichmentQueue;

    public WordService(GlosifyContext context, IAiEnrichmentQueue enrichmentQueue)
    {
        _context = context;
        _enrichmentQueue = enrichmentQueue;
    }

    public async Task<IReadOnlyList<Word>> GetWordsAsync(Guid quizId)
    {
        return await _context.Words
            .Where(w => w.QuizId == quizId)
            .OrderBy(w => w.Lemma)
            .ToListAsync();
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

    public async Task<IReadOnlyList<TypingQuizWordViewModel>> LoadWordsAsync(Guid quizId, int wordCount)
    {
        var take = Math.Clamp(wordCount, 1, 100);

        return await _context.Words
            .Where(word => word.QuizId == quizId)
            .GroupJoin(
                _context.WordDetails,
                word => word.WordDetailId,
                detail => detail.Id,
                (word, details) => new { Word = word, Detail = details.FirstOrDefault() })
            .OrderBy(_ => Guid.NewGuid())
            .Take(take)
            .Select(item => new TypingQuizWordViewModel
            {
                Id = item.Word.Id,
                Prompt = item.Word.Translation,
                Answer = item.Word.Lemma,
                ExampleSentence = item.Detail == null ? string.Empty : item.Detail.ExampleSentence,
                ExampleTranslation = item.Detail == null ? string.Empty : item.Detail.Explanation
            })
            .ToListAsync();
    }

    public async Task<IReadOnlyList<FlashcardWordViewModel>> LoadCardsAsync(Guid quizId, int wordCount)
    {
        var take = Math.Clamp(wordCount, 1, 100);

        return await _context.Words
            .Where(word => word.QuizId == quizId)
            .GroupJoin(
                _context.WordDetails,
                word => word.WordDetailId,
                detail => detail.Id,
                (word, details) => new { Word = word, Detail = details.FirstOrDefault() })
            .OrderBy(_ => Guid.NewGuid())
            .Take(take)
            .Select(item => new FlashcardWordViewModel
            {
                Id = item.Word.Id,
                Lemma = item.Word.Lemma,
                Translation = item.Word.Translation,
                ExampleSentence = item.Detail == null ? string.Empty : item.Detail.ExampleSentence,
                ExampleTranslation = item.Detail == null ? string.Empty : item.Detail.Explanation
            })
            .ToListAsync();
    }

    public async Task<IReadOnlyList<QuizSentenceViewModel>> GetSentencesAsync(Guid quizId)
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
            .GroupBy(detail => detail.ExampleSentence.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new QuizSentenceViewModel
            {
                Text = group.Key,
                Translation = group
                    .Select(detail => detail.Explanation.Trim())
                    .FirstOrDefault(translation => !string.IsNullOrWhiteSpace(translation)) ?? string.Empty,
                WordCount = group.Count()
            })
            .OrderBy(sentence => sentence.Text)
            .ToList();
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

        _enrichmentQueue.Enqueue(new AiEnrichmentJob(wordDetail.Id, targetLanguage, lemma));
        return true;
    }

    public async Task<bool> DeleteWordAsync(string wordId, string userId)
    {
        if (string.IsNullOrWhiteSpace(wordId))
            return false;

        var word = await _context.Words.FirstOrDefaultAsync(w => w.Id == wordId);
        if (word == null)
            return false;

        var quiz = await _context.Quizzes
            .FirstOrDefaultAsync(q => q.Id == word.QuizId && q.UserId == userId);

        if (quiz == null)
            return false;

        _context.Words.Remove(word);
        await _context.SaveChangesAsync();

        return true;
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
            CreatedAt = now,
            UpdatedAt = now
        };

        _context.WordDetails.Add(detail);
        return detail;
    }
}
