using System.Text.RegularExpressions;
using Glosify.Data;
using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Services;
using Glosify.Services.CustomQuizzes;
using Glosify.Services.Anki;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Words;

public class WordService : IWordService
{
    private readonly GlosifyContext _context;
    private readonly IAnkiCollectionService _ankiCollections;

    public WordService(GlosifyContext context, IAnkiCollectionService ankiCollections)
    {
        _context = context;
        _ankiCollections = ankiCollections;
    }

    public async Task<IReadOnlyList<Word>> GetWordsAsync(Guid quizId, CancellationToken cancellationToken = default)
    {
        return await _context.Words
            .AsNoTracking()
            .Where(w => w.QuizId == quizId)
            .OrderBy(w => w.CreatedAt)
            .ThenBy(w => w.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QuizCardData>> LoadCardsAsync(Guid quizId, int wordCount, int rangeStartPercent = 0, int rangeEndPercent = 100, CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(wordCount, 1, 1000);

        var orderedWords = await _context.Words
            .AsNoTracking()
            .Where(word => word.QuizId == quizId)
            .OrderBy(word => word.CreatedAt)
            .ThenBy(word => word.Id)
            .ToListAsync(cancellationToken);
        var pool = PracticeRange.Slice(orderedWords, rangeStartPercent, rangeEndPercent);

        var cards = pool
            .OrderBy(_ => Guid.NewGuid())
            .Take(take)
            .ToList();
        var sentences = await _context.QuizSentences
            .AsNoTracking()
            .Where(sentence => sentence.QuizId == quizId)
            .OrderBy(sentence => sentence.CreatedAt)
            .ThenBy(sentence => sentence.Id)
            .ToListAsync(cancellationToken);

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

    public async Task<IReadOnlyList<QuizCardData>> LoadSentenceCardsAsync(Guid quizId, int sentenceCount, int rangeStartPercent = 0, int rangeEndPercent = 100, CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(sentenceCount, 1, 1000);

        var orderedSentences = await _context.QuizSentences
            .AsNoTracking()
            .Where(sentence => sentence.QuizId == quizId)
            .OrderBy(sentence => sentence.CreatedAt)
            .ThenBy(sentence => sentence.Id)
            .ToListAsync(cancellationToken);
        var pool = PracticeRange.Slice(orderedSentences, rangeStartPercent, rangeEndPercent);

        return pool
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
            .ToList();
    }

    public async Task<IReadOnlyList<QuizCardData>> LoadCardsByIdsAsync(Guid quizId, IReadOnlyCollection<string> wordIds, CancellationToken cancellationToken = default)
    {
        if (wordIds.Count == 0)
            return [];

        var words = await _context.Words
            .AsNoTracking()
            .Where(word => word.QuizId == quizId && wordIds.Contains(word.Id))
            .OrderBy(_ => Guid.NewGuid())
            .ToListAsync(cancellationToken);
        var sentences = await _context.QuizSentences
            .AsNoTracking()
            .Where(sentence => sentence.QuizId == quizId)
            .OrderBy(sentence => sentence.CreatedAt)
            .ThenBy(sentence => sentence.Id)
            .ToListAsync(cancellationToken);

        return words
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

    public async Task<IReadOnlyList<QuizSentenceData>> GetSentencesAsync(Guid quizId, CancellationToken cancellationToken = default)
    {
        var words = await _context.Words
            .Where(word => word.QuizId == quizId)
            .Select(word => word.Lemma)
            .ToListAsync(cancellationToken);
        var sentences = await _context.QuizSentences
            .AsNoTracking()
            .Where(sentence => sentence.QuizId == quizId)
            .OrderBy(sentence => sentence.CreatedAt)
            .ThenBy(sentence => sentence.Id)
            .ToListAsync(cancellationToken);

        // One pattern per distinct word for the whole call; building the pattern
        // inside the per-sentence loop churned the small static regex cache.
        var wordRegexes = words
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(BuildWordRegex)
            .OfType<Regex>()
            .ToList();

        return sentences
            .Select(sentence => new QuizSentenceData
            {
                Id = sentence.Id,
                Text = CleanExampleForDisplay(sentence.Text),
                Translation = sentence.Translation.Trim(),
                WordCount = string.IsNullOrWhiteSpace(sentence.Text)
                    ? 0
                    : wordRegexes.Count(regex => regex.IsMatch(sentence.Text))
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

    public async Task<bool> AddWordAsync(
        Guid quizId,
        string word,
        string translation,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(word) || string.IsNullOrWhiteSpace(translation))
            return false;

        return await ExecuteAtomicallyAsync(async token =>
        {
            _context.Words.Add(new Word
            {
                Id = Guid.NewGuid().ToString("N"),
                QuizId = quizId,
                Lemma = word.Trim(),
                Translation = translation.Trim()
            });

            await _context.SaveChangesAsync(token);
            await _ankiCollections.SyncQuizAsync(quizId, token);
            return true;
        }, cancellationToken);
    }

    public async Task<Word?> DeleteWordAsync(string wordId, string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(wordId))
            return null;

        return await ExecuteAtomicallyAsync(async token =>
        {
            var word = await _context.Words.FirstOrDefaultAsync(w => w.Id == wordId, token);
            if (word == null)
                return null;

            var ownsQuiz = await _context.Quizzes
                .AnyAsync(q => q.Id == word.QuizId && q.UserId == userId, token);
            if (!ownsQuiz)
                return null;

            await new CustomQuizService(_context).PruneWordBindingsAsync(word.QuizId, [word.Id], token);
            _context.Words.Remove(word);
            await _context.SaveChangesAsync(token);
            await _ankiCollections.SyncQuizAsync(word.QuizId, token);
            return word;
        }, cancellationToken);
    }

    public async Task<QuizSentence?> DeleteSentenceAsync(Guid sentenceId, string userId, CancellationToken cancellationToken = default)
    {
        return await ExecuteAtomicallyAsync(async token =>
        {
            var sentence = await _context.QuizSentences.SingleOrDefaultAsync(item => item.Id == sentenceId, token);
            if (sentence is null || !await _context.Quizzes.AnyAsync(
                    quiz => quiz.Id == sentence.QuizId && quiz.UserId == userId, token))
                return null;
            _context.QuizSentences.Remove(sentence);
            await _context.SaveChangesAsync(token);
            await _ankiCollections.SyncQuizAsync(sentence.QuizId, token);
            return sentence;
        }, cancellationToken);
    }

    public async Task<bool> WordExistsAsync(Guid quizId, string word, CancellationToken cancellationToken = default)
    {
        return await _context.Words
            .AnyAsync(w => w.QuizId == quizId && w.Lemma == word, cancellationToken);
    }

    private static QuizSentence? ChooseSentenceForWord(string word, IReadOnlyList<QuizSentence> sentences)
    {
        var regex = BuildWordRegex(word);
        return regex == null
            ? null
            : sentences.FirstOrDefault(sentence =>
                !string.IsNullOrWhiteSpace(sentence.Text) && regex.IsMatch(sentence.Text));
    }

    private static Regex? BuildWordRegex(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return null;
        }

        var pattern = $@"(?<![\p{{L}}\p{{M}}]){Regex.Escape(word.Trim())}(?![\p{{L}}\p{{M}}])";
        return new Regex(pattern, RegexOptions.IgnoreCase);
    }

    private async Task<T> ExecuteAtomicallyAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        if (!_context.Database.IsRelational() || _context.Database.CurrentTransaction is not null)
            return await operation(cancellationToken);

        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async token =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(token);
            try
            {
                var result = await operation(token);
                await transaction.CommitAsync(token);
                return result;
            }
            catch
            {
                _context.ChangeTracker.Clear();
                throw;
            }
        }, cancellationToken);
    }
}
