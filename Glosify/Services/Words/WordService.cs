using Glosify.Data;
using Glosify.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace Glosify.Services;

public class WordService : IWordService
{
    private readonly GlosifyContext _context;
    private readonly IWordDetailService _wordDetailService;

    public WordService(GlosifyContext context, IWordDetailService wordDetailService)
    {
        _context = context;
        _wordDetailService = wordDetailService;
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
        var sentences = await _context.QuizSentences
            .Where(sentence => sentence.QuizId == quizId)
            .OrderBy(sentence => sentence.Text)
            .ToListAsync();

        return cards
            .Select(item => new QuizCardData
            {
                Id = item.Word.Id,
                Lemma = item.Word.Lemma,
                Translation = item.Word.Translation,
                ExampleSentence = CleanExampleForDisplay(ChooseSentenceForWord(item.Word.Lemma, sentences)?.Text ?? item.Detail?.ExampleSentence),
                ExampleTranslation = ChooseSentenceForWord(item.Word.Lemma, sentences)?.Translation ?? item.Detail?.ExampleSentenceTranslation ?? string.Empty
            })
            .ToList();
    }

    public async Task<IReadOnlyList<QuizSentenceData>> GetSentencesAsync(Guid quizId)
    {
        var words = await _context.Words
            .Where(word => word.QuizId == quizId)
            .Select(word => word.Lemma)
            .ToListAsync();
        var sentences = await _context.QuizSentences
            .Where(sentence => sentence.QuizId == quizId)
            .OrderBy(sentence => sentence.Text)
            .ToListAsync();

        return sentences
            .Select(sentence => new QuizSentenceData
            {
                Text = CleanExampleForDisplay(sentence.Text),
                Translation = sentence.Translation.Trim(),
                WordCount = CountLinkedWords(sentence.Text, words)
            })
            .Where(sentence => !string.IsNullOrWhiteSpace(sentence.Text))
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

        var trimmedWord = word.Trim();
        var cleanTranslation = translation.Trim();
        var wordDetail = await _wordDetailService.FindCachedAsync(
            sourceLanguage,
            targetLanguage,
            trimmedWord,
            cleanTranslation);

        _context.Words.Add(new Word
        {
            Id = Guid.NewGuid().ToString("N"),
            QuizId = quizId,
            Lemma = trimmedWord,
            Translation = cleanTranslation,
            WordDetailId = wordDetail?.Id
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

    public async Task<bool> WordExistsAsync(Guid quizId, string word)
    {
        return await _context.Words
            .AnyAsync(w => w.QuizId == quizId && w.Lemma == word);
    }

    private static QuizSentence? ChooseSentenceForWord(string word, IReadOnlyList<QuizSentence> sentences)
    {
        return sentences.FirstOrDefault(sentence => ContainsWord(sentence.Text, word));
    }

    private static int CountLinkedWords(string sentence, IReadOnlyList<string> words)
    {
        return words
            .Where(word => ContainsWord(sentence, word))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private static bool ContainsWord(string sentence, string word)
    {
        if (string.IsNullOrWhiteSpace(sentence) || string.IsNullOrWhiteSpace(word))
        {
            return false;
        }

        var pattern = $@"(?<![\p{{L}}\p{{M}}]){Regex.Escape(word.Trim())}(?![\p{{L}}\p{{M}}])";
        return Regex.IsMatch(sentence, pattern, RegexOptions.IgnoreCase);
    }
}
