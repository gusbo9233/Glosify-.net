using System.Text.Json;
using Glosify.Data;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services;

public sealed class ChangeApplier : IChangeApplier
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly GlosifyContext _context;
    private readonly ILogger<ChangeApplier> _logger;

    public ChangeApplier(
        GlosifyContext context,
        ILogger<ChangeApplier> logger)
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
                case PendingChangeKinds.AddSentence:
                    applied += await ApplyAddSentenceAsync(change.Payload, quiz, cancellationToken) ? 1 : 0;
                    break;
                case PendingChangeKinds.EditWord:
                    applied += await ApplyEditWordAsync(change.Payload, quiz, cancellationToken) ? 1 : 0;
                    break;
                case PendingChangeKinds.DeleteWord:
                    applied += await ApplyDeleteWordAsync(change.Payload, quiz, cancellationToken) ? 1 : 0;
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
        var newWord = GetString(payload, "word", "lemma");
        var translation = GetString(payload, "translation");
        if (string.IsNullOrWhiteSpace(newWord) || string.IsNullOrWhiteSpace(translation))
        {
            return false;
        }

        var exists = await _context.Words
            .AnyAsync(w => w.QuizId == quiz.Id && w.Lemma == newWord, ct);
        if (exists)
        {
            return false;
        }

        _context.Words.Add(new Word
        {
            Id = Guid.NewGuid().ToString("N"),
            QuizId = quiz.Id,
            Lemma = newWord,
            Translation = translation,
        });

        return true;
    }

    private async Task<bool> ApplyAddSentenceAsync(JsonElement payload, Quiz quiz, CancellationToken ct)
    {
        var text = GetString(payload, "text", "sentence");
        var translation = GetString(payload, "translation");
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(translation))
        {
            return false;
        }

        return await AddQuizSentenceAsync(quiz.Id, text, translation, ct);
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

        var newWord = GetString(payload, "word", "lemma");
        var newTranslation = GetString(payload, "translation");
        if (!string.IsNullOrWhiteSpace(newWord)) word.Lemma = newWord;
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

    private async Task<int> ApplyRepairSentenceAsync(JsonElement payload, Quiz quiz, CancellationToken ct)
    {
        var original = GetString(payload, "original_text");
        var newText = GetString(payload, "new_text");
        var newTranslation = GetString(payload, "new_translation");
        if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(newText))
        {
            return 0;
        }

        var sentences = await _context.QuizSentences
            .Where(sentence => sentence.QuizId == quiz.Id)
            .ToListAsync(ct);
        var matches = sentences
            .Where(row => string.Equals(
                row.Text,
                original,
                StringComparison.Ordinal))
            .ToList();

        foreach (var sentence in matches)
        {
            sentence.Text = newText;
            sentence.Translation = newTranslation;
        }
        return matches.Count;
    }

    private static string GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var p)) return string.Empty;
        return p.ValueKind == JsonValueKind.String ? p.GetString() ?? string.Empty : string.Empty;
    }

    private static string GetString(JsonElement element, string preferredProperty, string legacyProperty)
    {
        var preferred = GetString(element, preferredProperty);
        return string.IsNullOrWhiteSpace(preferred) ? GetString(element, legacyProperty) : preferred;
    }

    private async Task<bool> AddQuizSentenceAsync(Guid quizId, string text, string translation, CancellationToken ct)
    {
        var cleanText = text.Trim();
        if (string.IsNullOrWhiteSpace(cleanText)
            || _context.QuizSentences.Local.Any(sentence =>
                sentence.QuizId == quizId
                && string.Equals(sentence.Text, cleanText, StringComparison.OrdinalIgnoreCase))
            || await _context.QuizSentences.AnyAsync(sentence =>
                sentence.QuizId == quizId
                && sentence.Text == cleanText,
                ct))
        {
            return false;
        }

        _context.QuizSentences.Add(new QuizSentence
        {
            Id = Guid.NewGuid(),
            QuizId = quizId,
            Text = cleanText,
            Translation = translation.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        });
        return true;
    }
}
