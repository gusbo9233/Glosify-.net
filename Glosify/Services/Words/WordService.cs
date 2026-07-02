using Glosify.Data;
using Glosify.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

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
            .OrderBy(w => w.CreatedAt)
            .ThenBy(w => w.Id)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<QuizCardData>> LoadCardsAsync(Guid quizId, int wordCount)
    {
        var take = Math.Clamp(wordCount, 1, 100);

        var cards = await _context.Words
            .Where(word => word.QuizId == quizId)
            .OrderBy(_ => Guid.NewGuid())
            .Take(take)
            .ToListAsync();
        var sentences = await _context.QuizSentences
            .Where(sentence => sentence.QuizId == quizId)
            .OrderBy(sentence => sentence.CreatedAt)
            .ThenBy(sentence => sentence.Id)
            .ToListAsync();

        return cards
            .Select(item =>
            {
                var sentence = ChooseSentenceForWord(item.Lemma, sentences);
                return new QuizCardData
                {
                    Id = item.Id,
                    Lemma = item.Lemma,
                    Translation = item.Translation,
                    ExampleSentence = CleanExampleForDisplay(sentence?.Text),
                    ExampleTranslation = sentence?.Translation ?? string.Empty
                };
            })
            .ToList();
    }

    public async Task<IReadOnlyList<QuizCardData>> LoadSentenceCardsAsync(Guid quizId, int sentenceCount)
    {
        var take = Math.Clamp(sentenceCount, 1, 100);

        return await _context.QuizSentences
            .Where(sentence => sentence.QuizId == quizId)
            .OrderBy(_ => Guid.NewGuid())
            .Take(take)
            .Select(sentence => new QuizCardData
            {
                Id = sentence.Id.ToString("N"),
                Lemma = sentence.Text.Trim(),
                Translation = sentence.Translation.Trim(),
                ExampleSentence = string.Empty,
                ExampleTranslation = string.Empty
            })
            .ToListAsync();
    }

    public async Task<IReadOnlyList<QuizSentenceData>> GetSentencesAsync(Guid quizId)
    {
        var words = await _context.Words
            .Where(word => word.QuizId == quizId)
            .Select(word => word.Lemma)
            .ToListAsync();
        var sentences = await _context.QuizSentences
            .Where(sentence => sentence.QuizId == quizId)
            .OrderBy(sentence => sentence.CreatedAt)
            .ThenBy(sentence => sentence.Id)
            .ToListAsync();

        return sentences
            .Select(sentence => new QuizSentenceData
            {
                Id = sentence.Id,
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
            : exampleSentence.Trim();
    }

    public async Task<bool> AddWordAsync(Guid quizId, string word, string translation, string sourceLanguage, string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(word) || string.IsNullOrWhiteSpace(translation))
            return false;

        _context.Words.Add(new Word
        {
            Id = Guid.NewGuid().ToString("N"),
            QuizId = quizId,
            Lemma = word.Trim(),
            Translation = translation.Trim()
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
