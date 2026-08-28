using Glosify.Models.Entities;
using System.Text.Json;

namespace Glosify.Services.Ai.Assistant;

internal sealed class AssistantMessagePresenter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string NormalizeTitle(string? title)
    {
        var cleaned = string.Join(
            " ",
            (title ?? string.Empty).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return AssistantThreadDefaults.NewChatTitle;
        }

        return cleaned.Length <= 64 ? cleaned : cleaned[..64] + "...";
    }

    public bool HasVisibleContent(AssistantMessage message) =>
        !string.IsNullOrWhiteSpace(ExtractVisibleText(message))
        || !string.IsNullOrWhiteSpace(message.PendingChangesJson);

    public string ExtractVisibleText(AssistantMessage message)
    {
        try
        {
            var content = JsonSerializer.Deserialize<PresentedContent>(message.ContentJson, JsonOptions);
            var parts = content?.Parts ?? [];
            if (parts.Any(part => part.Kind != "text"))
            {
                return string.Empty;
            }

            return string.Join("\n", parts
                .Select(part => part.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text)));
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    public IReadOnlyList<string> GetReferencedWordIds(IEnumerable<PendingChange> changes) =>
        changes
            .Select(change => GetString(change.Payload, "word_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .ToList();

    public AssistantPendingChangeView PresentPendingChange(
        PendingChange change,
        IReadOnlyDictionary<string, AssistantWordLabel> wordLabels)
    {
        var rawPayload = change.Payload.ValueKind == JsonValueKind.Undefined
            ? "{}"
            : change.Payload.GetRawText();
        return new AssistantPendingChangeView(change.Kind, BuildSummary(change, wordLabels), rawPayload);
    }

    private static string BuildSummary(
        PendingChange change,
        IReadOnlyDictionary<string, AssistantWordLabel> wordLabels)
    {
        if (RetiredPendingChangeKinds.ContainsCustomQuizChange(change))
        {
            return "Custom quiz change (no longer available)";
        }

        try
        {
            return change.Kind switch
            {
                PendingChangeKinds.AddWord => BuildAddWordSummary(change.Payload),
                PendingChangeKinds.AddSentence => BuildAddSentenceSummary(change.Payload),
                PendingChangeKinds.EditWord => BuildEditWordSummary(change.Payload, wordLabels),
                PendingChangeKinds.EditSentence => BuildEditSentenceSummary(change.Payload),
                PendingChangeKinds.DeleteWord => $"Remove {GetWordDisplay(change.Payload, wordLabels)}",
                PendingChangeKinds.DeleteSentence => BuildDeleteSentenceSummary(change.Payload),
                PendingChangeKinds.CreateQuiz => BuildCreateQuizSummary(change.Payload),
                PendingChangeKinds.CreateCollection => BuildCreateCollectionSummary(change.Payload),
                PendingChangeKinds.MoveQuiz => BuildMoveQuizSummary(change.Payload),
                PendingChangeKinds.RenameCollection => BuildRenameCollectionSummary(change.Payload),
                PendingChangeKinds.MoveCollection => BuildMoveCollectionSummary(change.Payload),
                _ => change.Kind,
            };
        }
        catch
        {
            return change.Kind;
        }
    }

    private static string BuildAddWordSummary(JsonElement payload)
    {
        return $"Add {GetString(payload, "word")} -> {GetString(payload, "translation")}";
    }

    private static string BuildAddSentenceSummary(JsonElement payload)
    {
        var text = TruncateValue(GetString(payload, "text"), 90);
        var translation = TruncateValue(GetString(payload, "translation"), 90);
        return string.IsNullOrWhiteSpace(translation)
            ? $"Add sentence \"{text}\""
            : $"Add sentence \"{text}\" ({translation})";
    }

    private static string BuildEditWordSummary(
        JsonElement payload,
        IReadOnlyDictionary<string, AssistantWordLabel> wordLabels)
    {
        var wordId = GetString(payload, "word_id");
        wordLabels.TryGetValue(wordId, out var label);

        var originalWord = FirstNonEmpty(GetString(payload, "original_word"), label?.Word);
        var originalTranslation = FirstNonEmpty(GetString(payload, "original_translation"), label?.Translation);
        var newWord = FirstNonEmpty(GetString(payload, "word"), originalWord);
        var newTranslation = FirstNonEmpty(GetString(payload, "translation"), originalTranslation);

        var changes = new List<string>();
        if (!string.IsNullOrWhiteSpace(originalWord)
            && !string.IsNullOrWhiteSpace(newWord)
            && !string.Equals(originalWord, newWord, StringComparison.Ordinal))
        {
            changes.Add($"{originalWord} -> {newWord}");
        }

        if (!string.IsNullOrWhiteSpace(originalTranslation)
            && !string.IsNullOrWhiteSpace(newTranslation)
            && !string.Equals(originalTranslation, newTranslation, StringComparison.Ordinal))
        {
            changes.Add($"{originalTranslation} -> {newTranslation}");
        }

        if (changes.Count > 0)
        {
            return $"Edit {string.Join("; ", changes)}";
        }

        if (!string.IsNullOrWhiteSpace(originalWord) || !string.IsNullOrWhiteSpace(originalTranslation))
        {
            return $"Edit {FormatWordPair(originalWord, originalTranslation)}";
        }

        return $"Edit {GetWordDisplay(payload, wordLabels)}";
    }

    private static string BuildEditSentenceSummary(JsonElement payload)
    {
        var originalText = TruncateValue(GetString(payload, "original_text"), 60);
        var newText = TruncateValue(FirstNonEmpty(GetString(payload, "text"), originalText), 60);
        var originalTranslation = TruncateValue(GetString(payload, "original_translation"), 60);
        var newTranslation = TruncateValue(
            FirstNonEmpty(GetString(payload, "translation"), originalTranslation),
            60);

        var changes = new List<string>();
        if (!string.Equals(originalText, newText, StringComparison.Ordinal))
        {
            changes.Add($"\"{originalText}\" -> \"{newText}\"");
        }
        if (!string.Equals(originalTranslation, newTranslation, StringComparison.Ordinal))
        {
            changes.Add($"\"{originalTranslation}\" -> \"{newTranslation}\"");
        }

        return changes.Count == 0
            ? $"Edit sentence \"{originalText}\""
            : $"Edit sentence {string.Join("; ", changes)}";
    }

    private static string BuildDeleteSentenceSummary(JsonElement payload)
    {
        var text = TruncateValue(GetString(payload, "text"), 90);
        return string.IsNullOrWhiteSpace(text)
            ? "Remove sentence"
            : $"Remove sentence \"{text}\"";
    }

    private static string BuildCreateQuizSummary(JsonElement payload)
    {
        var name = GetString(payload, "name");
        var source = GetString(payload, "source_language");
        var target = GetString(payload, "target_language");
        var contents = DescribeStarterContent(payload);
        return $"Create quiz \"{name}\"{contents} ({source} -> {target})";
    }

    /// <summary>
    /// The " with 12 words and 3 sentences" clause of a create-quiz summary.
    /// </summary>
    /// <remarks>
    /// Counts rather than the content itself: a proposal can carry a hundred of each, and the
    /// stored payload stays the authoritative detail behind the card. An empty collection is
    /// left out entirely so a word-only quiz reads as it always has.
    /// </remarks>
    private static string DescribeStarterContent(JsonElement payload)
    {
        var parts = new List<string>(2);
        AppendCount(parts, CountArray(payload, "words"), "word");
        AppendCount(parts, CountArray(payload, "sentences"), "sentence");
        return parts.Count == 0 ? string.Empty : $" with {string.Join(" and ", parts)}";

        static void AppendCount(List<string> parts, int count, string noun)
        {
            if (count > 0)
            {
                parts.Add($"{count} {noun}{(count == 1 ? string.Empty : "s")}");
            }
        }
    }

    private static int CountArray(JsonElement payload, string property) =>
        payload.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;

    private static string BuildCreateCollectionSummary(JsonElement payload)
    {
        var name = GetString(payload, "name");
        var language = GetString(payload, "language");
        return $"Create collection \"{name}\" in {language}";
    }

    private static string BuildMoveQuizSummary(JsonElement payload)
    {
        var quizName = GetString(payload, "quiz_name");
        var collectionName = GetString(payload, "collection_name");
        return string.IsNullOrWhiteSpace(collectionName)
            ? $"Move quiz \"{quizName}\" to the library root"
            : $"Move quiz \"{quizName}\" to collection \"{collectionName}\"";
    }

    private static string BuildRenameCollectionSummary(JsonElement payload)
    {
        var originalName = GetString(payload, "original_name");
        var name = GetString(payload, "name");
        return $"Rename collection \"{originalName}\" to \"{name}\"";
    }

    private static string BuildMoveCollectionSummary(JsonElement payload)
    {
        var collectionName = GetString(payload, "collection_name");
        var parentName = GetString(payload, "parent_collection_name");
        return string.IsNullOrWhiteSpace(parentName)
            ? $"Move collection \"{collectionName}\" to the library root"
            : $"Move collection \"{collectionName}\" under \"{parentName}\"";
    }

    private static string GetWordDisplay(
        JsonElement payload,
        IReadOnlyDictionary<string, AssistantWordLabel> wordLabels)
    {
        var wordId = GetString(payload, "word_id");
        if (!string.IsNullOrWhiteSpace(wordId) && wordLabels.TryGetValue(wordId, out var label))
        {
            return $"{label.Word} -> {label.Translation}";
        }

        return "this word";
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static string FormatWordPair(string? word, string? translation)
    {
        if (!string.IsNullOrWhiteSpace(word) && !string.IsNullOrWhiteSpace(translation))
        {
            return $"{word} -> {translation}";
        }

        return string.IsNullOrWhiteSpace(word) ? translation ?? string.Empty : word;
    }

    public string Truncate(string? value, int max) => TruncateValue(value, max);

    private static string TruncateValue(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= max ? value : value[..max] + "...";
    }

    private static string GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? string.Empty
            : string.Empty;
    }

    public IReadOnlyList<PendingChange> ParseStoredChanges(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<PendingChange>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed class PresentedContent
    {
        public List<PresentedPart>? Parts { get; set; }
    }

    private sealed class PresentedPart
    {
        public string Kind { get; set; } = "text";
        public string? Text { get; set; }
    }
}

internal sealed record AssistantWordLabel(string Id, string Word, string Translation);
