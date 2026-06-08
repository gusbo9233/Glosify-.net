using System.Text.Json;
using Glosify.Data;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services;

public sealed class ChangeApplier : IChangeApplier
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly GlosifyContext _context;
    private readonly IWordDetailService _wordDetailService;
    private readonly ILogger<ChangeApplier> _logger;

    public ChangeApplier(
        GlosifyContext context,
        IWordDetailService wordDetailService,
        ILogger<ChangeApplier> logger)
    {
        _context = context;
        _wordDetailService = wordDetailService;
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

        var wordDetail = await _wordDetailService.FindCachedAsync(
            quiz.SourceLanguage,
            quiz.TargetLanguage,
            newWord,
            translation,
            ct);

        _context.Words.Add(new Word
        {
            Id = Guid.NewGuid().ToString("N"),
            QuizId = quiz.Id,
            Lemma = newWord,
            Translation = translation,
            WordDetailId = wordDetail?.Id,
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
        var properties = NormalizeProperties(payload);
        var variants = NormalizeVariants(payload);
        var changed = false;

        if (!string.IsNullOrWhiteSpace(properties) && !IsEmptyJsonObject(properties))
        {
            detail.Properties = properties;
            changed = true;
        }
        if (!string.IsNullOrWhiteSpace(variants) && !IsEmptyJsonArray(variants))
        {
            detail.Variants = variants;
            changed = true;
        }
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

    private static string NormalizeProperties(JsonElement payload)
    {
        var properties = GetJsonArgument(payload, "properties", "properties_json");
        if (properties == null || properties.Value.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        var normalized = new Dictionary<string, JsonElement>();
        foreach (var property in properties.Value.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            var key = NormalizePropertyKey(property.Name);
            if (!string.IsNullOrWhiteSpace(key))
            {
                normalized[key] = property.Value.Clone();
            }
        }

        return normalized.Count == 0 ? string.Empty : JsonSerializer.Serialize(normalized, JsonOptions);
    }

    private static string NormalizeVariants(JsonElement payload)
    {
        var variants = GetJsonArgument(payload, "variants", "variants_json");
        if (variants == null || variants.Value.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var normalized = new List<GeneratedWordVariant>();
        foreach (var item in variants.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var form = item.TryGetProperty("form", out var formElement) && formElement.ValueKind == JsonValueKind.String
                ? formElement.GetString()?.Trim()
                : string.Empty;
            if (string.IsNullOrWhiteSpace(form))
            {
                continue;
            }

            var tags = item.TryGetProperty("tags", out var tagsElement)
                ? NormalizeTags(tagsElement)
                : [];
            normalized.Add(new GeneratedWordVariant
            {
                Form = form,
                Label = GetString(item, "label").Trim(),
                Group = GetString(item, "group").Trim(),
                Tags = tags,
            });
        }

        return normalized.Count == 0 ? string.Empty : JsonSerializer.Serialize(normalized, JsonOptions);
    }

    private static JsonElement? GetJsonArgument(JsonElement payload, string propertyName, string jsonStringName)
    {
        if (payload.TryGetProperty(propertyName, out var structured)
            && structured.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            return structured;
        }

        var rawJson = GetString(payload, jsonStringName);
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<string> NormalizeTags(JsonElement tagsElement)
    {
        var tags = new List<string>();
        if (tagsElement.ValueKind == JsonValueKind.String)
        {
            AddTag(tags, tagsElement.GetString()?.Trim());
            return tags;
        }

        if (tagsElement.ValueKind != JsonValueKind.Array)
        {
            return tags;
        }

        foreach (var tagElement in tagsElement.EnumerateArray())
        {
            if (tagElement.ValueKind == JsonValueKind.String)
            {
                AddTag(tags, tagElement.GetString()?.Trim());
            }
        }

        return tags;
    }

    private static void AddTag(List<string> tags, string? tag)
    {
        if (!string.IsNullOrWhiteSpace(tag)
            && !tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            tags.Add(tag);
        }
    }

    private static string NormalizePropertyKey(string key)
    {
        var normalized = key.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        var compact = normalized.Replace("_", string.Empty);

        return compact switch
        {
            "pos" or "partofspeech" or "wordclass" => "pos",
            "grammaticalgender" => "gender",
            _ => normalized.Trim('_')
        };
    }

    private static bool IsEmptyJsonObject(string json) => !WordDetailJsonReader.ReadProperties(json).Any();

    private static bool IsEmptyJsonArray(string json) => !WordDetailJsonReader.ReadVariants(json).Any();

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
