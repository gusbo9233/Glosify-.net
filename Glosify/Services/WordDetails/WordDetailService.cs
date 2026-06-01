using Glosify.Data;
using Glosify.Models;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services;

public sealed class WordDetailService : IWordDetailService
{
    private readonly GlosifyContext _context;

    public WordDetailService(GlosifyContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<WordDetail>> ListForUserAsync(string userId)
    {
        return await _context.WordDetails
            .Join(
                _context.Words.Join(
                    _context.Quizzes.Where(q => q.UserId == userId),
                    word => word.QuizId,
                    quiz => quiz.Id,
                    (word, _) => word.WordDetailId),
                wordDetail => wordDetail.Id,
                wordDetailId => wordDetailId!,
                (wordDetail, _) => wordDetail)
            .Distinct()
            .ToListAsync();
    }

    public async Task<OwnedWordDetail?> LoadOwnedAsync(string id, string userId)
    {
        var pair = await (
            from word in _context.Words
            join quiz in _context.Quizzes on word.QuizId equals quiz.Id
            join detail in _context.WordDetails on word.WordDetailId equals detail.Id
            where detail.Id == id && quiz.UserId == userId
            select new { detail, quiz }).FirstOrDefaultAsync();

        return pair == null ? null : new OwnedWordDetail(pair.detail, pair.quiz);
    }

    public async Task<OwnedWordDetailWithWord?> LoadOwnedWithWordAsync(string id, string userId)
    {
        var pair = await (
            from word in _context.Words
            join quiz in _context.Quizzes on word.QuizId equals quiz.Id
            join detail in _context.WordDetails on word.WordDetailId equals detail.Id
            where detail.Id == id && quiz.UserId == userId
            select new { detail, word, quiz }).FirstOrDefaultAsync();

        return pair == null ? null : new OwnedWordDetailWithWord(pair.detail, pair.word, pair.quiz);
    }

    public async Task<OwnedWord?> LoadOwnedWordAsync(string id, string userId, CancellationToken cancellationToken = default)
    {
        var pair = await (
            from word in _context.Words
            join quiz in _context.Quizzes on word.QuizId equals quiz.Id
            where word.Id == id && quiz.UserId == userId
            select new { word, quiz }).FirstOrDefaultAsync(cancellationToken);

        return pair == null ? null : new OwnedWord(pair.word, pair.quiz);
    }

    public async Task<WordDetail?> FindCachedAsync(
        string sourceLanguage,
        string targetLanguage,
        string word,
        string translation,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(word) || string.IsNullOrWhiteSpace(translation))
        {
            return null;
        }

        var key = WordDetailKey.Create(sourceLanguage, targetLanguage, word, translation);
        var exact = await _context.WordDetails.FirstOrDefaultAsync(detail => detail.Id == key.Id, cancellationToken);
        if (exact != null)
        {
            return exact;
        }

        var candidates = await _context.WordDetails
            .Where(detail =>
                detail.SourceLanguage == key.SourceLanguage
                && detail.TargetLanguage == key.TargetLanguage
                && detail.NormalizedTranslationHash == key.NormalizedTranslationHash)
            .ToListAsync(cancellationToken);

        return candidates.FirstOrDefault(detail => HasVariant(detail, key.NormalizedWord));
    }

    public async Task<WordDetail> GetOrCreateAndLinkAsync(
        Word word,
        Quiz quiz,
        string wordDetailWord,
        CancellationToken cancellationToken = default)
    {
        var key = WordDetailKey.Create(
            quiz.SourceLanguage,
            quiz.TargetLanguage,
            wordDetailWord,
            word.Translation);
        var detail = await _context.WordDetails.FirstOrDefaultAsync(row => row.Id == key.Id, cancellationToken);
        if (detail == null)
        {
            var now = DateTimeOffset.UtcNow;
            detail = new WordDetail
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
        }

        word.WordDetailId = detail.Id;
        return detail;
    }

    private static bool HasVariant(WordDetail detail, string normalizedWord)
    {
        return WordDetailJsonReader.ReadVariants(detail.Variants)
            .Any(variant => string.Equals(
                WordDetailKey.Normalize(variant.Form),
                normalizedWord,
                StringComparison.Ordinal));
    }

    public async Task<bool> HasReferencesAsync(string id)
    {
        return await _context.Words.AnyAsync(word => word.WordDetailId == id);
    }

    public async Task<WordDetail?> CreateAsync(CreateWordDetailInput input)
    {
        var key = WordDetailKey.Create(input.SourceLanguage, input.TargetLanguage, input.Word, input.Translation);

        if (await _context.WordDetails.AnyAsync(detail => detail.Id == key.Id))
        {
            // Shared cache row already exists; surface the existing entry rather than creating a duplicate.
            return null;
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
            Language = string.IsNullOrWhiteSpace(input.Language) ? key.TargetLanguage : input.Language.Trim(),
            ExampleSentence = input.ExampleSentence ?? string.Empty,
            ExampleSentenceTranslation = input.ExampleSentenceTranslation ?? string.Empty,
            Explanation = input.Explanation ?? string.Empty,
            Properties = "{}",
            Variants = string.IsNullOrWhiteSpace(input.Variants) ? "[]" : input.Variants,
            CreatedAt = now,
            UpdatedAt = now
        };

        _context.WordDetails.Add(detail);
        await _context.SaveChangesAsync();
        return detail;
    }

    public async Task<bool> UpdateAsync(EditWordDetailInput input, string userId)
    {
        var owned = await LoadOwnedAsync(input.Id, userId);
        if (owned == null)
        {
            return false;
        }
        var existing = owned.Detail;

        existing.ExampleSentence = input.ExampleSentence ?? string.Empty;
        existing.ExampleSentenceTranslation = input.ExampleSentenceTranslation ?? string.Empty;
        existing.Explanation = input.Explanation ?? string.Empty;
        existing.Variants = input.Variants ?? string.Empty;
        existing.Language = input.Language ?? string.Empty;
        existing.UpdatedAt = DateTimeOffset.UtcNow;

        try
        {
            await _context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // If the row vanished, treat as not-found; otherwise propagate to the global error handler.
            if (!await _context.WordDetails.AnyAsync(e => e.Id == input.Id))
            {
                return false;
            }
            throw;
        }
    }

    public async Task<bool> DeleteAsync(string id, string userId)
    {
        var owned = await LoadOwnedAsync(id, userId);
        if (owned == null)
        {
            return false;
        }

        if (await HasReferencesAsync(id))
        {
            return false;
        }

        _context.WordDetails.Remove(owned.Detail);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await _context.SaveChangesAsync(cancellationToken);
    }
}
