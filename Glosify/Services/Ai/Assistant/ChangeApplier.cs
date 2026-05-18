using System.Text.Json;
using Glosify.Data;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services;

public sealed class ChangeApplier : IChangeApplier
{
    private readonly GlosifyContext _context;
    private readonly ILogger<ChangeApplier> _logger;

    public ChangeApplier(GlosifyContext context, ILogger<ChangeApplier> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<int> ApplyAsync(
        Guid quizId,
        string userId,
        IReadOnlyList<PendingChange> changes,
        CancellationToken cancellationToken)
    {
        var quiz = await _context.Quizzes.FirstOrDefaultAsync(q => q.Id == quizId && q.UserId == userId, cancellationToken)
            ?? throw new QuizNotFoundException();

        var applied = 0;
        foreach (var change in changes)
        {
            switch (change.Kind)
            {
                case PendingChangeKinds.AddWord:
                    applied += await ApplyAddWordAsync(change.Payload, quiz, cancellationToken) ? 1 : 0;
                    break;
                case PendingChangeKinds.EditWord:
                    applied += await ApplyEditWordAsync(change.Payload, quiz, cancellationToken) ? 1 : 0;
                    break;
                case PendingChangeKinds.DeleteWord:
                    applied += await ApplyDeleteWordAsync(change.Payload, quiz, cancellationToken) ? 1 : 0;
                    break;
                case PendingChangeKinds.SetWordDetail:
                    applied += await ApplySetWordDetailAsync(change.Payload, quiz, cancellationToken) ? 1 : 0;
                    break;
                case PendingChangeKinds.RepairSentence:
                    applied += await ApplyRepairSentenceAsync(change.Payload, quiz, cancellationToken);
                    break;
                default:
                    _logger.LogWarning("Unknown pending change kind {Kind}; skipping.", change.Kind);
                    break;
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        return applied;
    }

    private async Task<bool> ApplyAddWordAsync(JsonElement payload, Quiz quiz, CancellationToken ct)
    {
        var lemma = GetString(payload, "lemma");
        var translation = GetString(payload, "translation");
        if (string.IsNullOrWhiteSpace(lemma) || string.IsNullOrWhiteSpace(translation))
        {
            return false;
        }

        var exists = await _context.Words
            .AnyAsync(w => w.QuizId == quiz.Id && w.Lemma == lemma, ct);
        if (exists)
        {
            return false;
        }

        var wordDetail = await GetOrCreateWordDetailAsync(quiz, lemma, translation, ct);

        var exampleSentence = GetString(payload, "example_sentence");
        var exampleSentenceTranslation = GetString(payload, "example_sentence_translation");
        if (!string.IsNullOrWhiteSpace(exampleSentence) && string.IsNullOrWhiteSpace(wordDetail.ExampleSentence))
        {
            wordDetail.ExampleSentence = exampleSentence;
            wordDetail.ExampleSentenceTranslation = exampleSentenceTranslation;
            wordDetail.UpdatedAt = DateTimeOffset.UtcNow;
        }

        _context.Words.Add(new Word
        {
            Id = Guid.NewGuid().ToString("N"),
            QuizId = quiz.Id,
            Lemma = lemma,
            Translation = translation,
            WordDetailId = wordDetail.Id,
        });
        return true;
    }

    private async Task<bool> ApplyEditWordAsync(JsonElement payload, Quiz quiz, CancellationToken ct)
    {
        var wordId = GetString(payload, "word_id");
        if (string.IsNullOrWhiteSpace(wordId))
        {
            return false;
        }

        var word = await _context.Words.FirstOrDefaultAsync(w => w.Id == wordId && w.QuizId == quiz.Id, ct);
        if (word == null)
        {
            return false;
        }

        var newLemma = GetString(payload, "lemma");
        var newTranslation = GetString(payload, "translation");
        if (!string.IsNullOrWhiteSpace(newLemma)) word.Lemma = newLemma;
        if (!string.IsNullOrWhiteSpace(newTranslation)) word.Translation = newTranslation;
        return true;
    }

    private async Task<bool> ApplyDeleteWordAsync(JsonElement payload, Quiz quiz, CancellationToken ct)
    {
        var wordId = GetString(payload, "word_id");
        if (string.IsNullOrWhiteSpace(wordId))
        {
            return false;
        }
        var word = await _context.Words.FirstOrDefaultAsync(w => w.Id == wordId && w.QuizId == quiz.Id, ct);
        if (word == null) return false;
        _context.Words.Remove(word);
        return true;
    }

    private async Task<bool> ApplySetWordDetailAsync(JsonElement payload, Quiz quiz, CancellationToken ct)
    {
        var wordId = GetString(payload, "word_id");
        if (string.IsNullOrWhiteSpace(wordId))
        {
            return false;
        }
        var word = await _context.Words.FirstOrDefaultAsync(w => w.Id == wordId && w.QuizId == quiz.Id, ct);
        if (word == null) return false;
        var detail = await _context.WordDetails.FirstOrDefaultAsync(d => d.Id == word.WordDetailId, ct);
        if (detail == null) return false;

        var explanation = GetString(payload, "explanation");
        var exampleSentence = GetString(payload, "example_sentence");
        var exampleSentenceTranslation = GetString(payload, "example_sentence_translation");
        var changed = false;

        if (!string.IsNullOrWhiteSpace(explanation))
        {
            detail.Explanation = explanation;
            changed = true;
        }
        if (!string.IsNullOrWhiteSpace(exampleSentence))
        {
            detail.ExampleSentence = exampleSentence;
            changed = true;
        }
        if (!string.IsNullOrWhiteSpace(exampleSentenceTranslation))
        {
            detail.ExampleSentenceTranslation = exampleSentenceTranslation;
            changed = true;
        }

        if (changed)
        {
            detail.UpdatedAt = DateTimeOffset.UtcNow;
        }
        return changed;
    }

    private async Task<int> ApplyRepairSentenceAsync(JsonElement payload, Quiz quiz, CancellationToken ct)
    {
        var original = GetString(payload, "original_text");
        var newText = GetString(payload, "new_text");
        var newTranslation = GetString(payload, "new_translation");
        if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(newText))
        {
            return 0;
        }

        var details = await _context.Words
            .Where(w => w.QuizId == quiz.Id)
            .Join(_context.WordDetails, w => w.WordDetailId, d => d.Id, (_, d) => d)
            .Where(d => d.ExampleSentence == original)
            .Distinct()
            .ToListAsync(ct);

        foreach (var detail in details)
        {
            detail.ExampleSentence = newText;
            detail.ExampleSentenceTranslation = newTranslation;
            detail.UpdatedAt = DateTimeOffset.UtcNow;
        }
        return details.Count;
    }

    private async Task<WordDetail> GetOrCreateWordDetailAsync(Quiz quiz, string lemma, string translation, CancellationToken ct)
    {
        var key = WordDetailKey.Create(quiz.SourceLanguage, quiz.TargetLanguage, lemma, translation);
        var existing = await _context.WordDetails.FirstOrDefaultAsync(d => d.Id == key.Id, ct);
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
            UpdatedAt = now,
        };
        _context.WordDetails.Add(detail);
        return detail;
    }

    private static string GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var p)) return string.Empty;
        return p.ValueKind == JsonValueKind.String ? p.GetString() ?? string.Empty : string.Empty;
    }
}
