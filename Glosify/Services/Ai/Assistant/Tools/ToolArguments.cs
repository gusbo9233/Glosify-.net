using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Glosify.Services.Ai.Assistant.Tools;

/// <summary>
/// Reading the model's tool arguments, which arrive as free-form JSON.
/// </summary>
/// <remarks>
/// Every helper here is total: a missing, null or wrong-typed property yields a default or
/// a skipped-item note rather than an exception, because the caller is a language model and
/// a malformed argument is an ordinary event, not a bug.
/// <para>
/// Moved verbatim out of the 2,991-line AssistantTools so the per-tool classes can share it.
/// </para>
/// </remarks>
internal static class ToolArguments
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonElement EmptyObject = JsonSerializer.SerializeToElement(new { });

    internal const int ListPageSize = 200;

    internal static bool TryGetArray(JsonElement element, string property, out JsonElement array)
    {
        if (element.TryGetProperty(property, out array) && array.ValueKind == JsonValueKind.Array)
        {
            return true;
        }
        array = default;
        return false;
    }

    internal static JsonElement ParseArgs(string argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
        {
            return EmptyObject;
        }
        try
        {
            using var document = JsonDocument.Parse(argsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : EmptyObject;
        }
        catch (JsonException)
        {
            return EmptyObject;
        }
    }

    internal static int GetOffset(JsonElement element)
    {
        if (element.TryGetProperty("offset", out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var offset)
            && offset > 0)
        {
            return offset;
        }

        return 0;
    }

    internal static int GetBoundedInt(
        JsonElement element,
        string property,
        int defaultValue,
        int min,
        int max)
    {
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var parsed))
        {
            return defaultValue;
        }

        return Math.Clamp(parsed, min, max);
    }

    // Distinguishes "not asked for" from a value, which GetBoundedInt cannot: a tool that
    // accepts several mutually exclusive coordinates has to know which one was supplied.
    internal static int? GetOptionalInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var parsed) => parsed,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    // Timestamps come back to us as strings the model copied out of an earlier tool
    // result, so anything round-trippable counts; an unparseable value is ignored rather
    // than treated as an error.
    internal static DateTimeOffset? GetTimestamp(JsonElement element, string property)
    {
        var text = GetString(element, property);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    internal static string? GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    // Models sometimes send a boolean flag as the string "true" instead of a JSON
    // boolean, so both spellings have to count.
    internal static bool GetBool(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return false;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            _ => false,
        };
    }

    // Models sometimes pass "" for arguments they mean to omit, so blank must fall
    // back the same way as missing.
    internal static string? FirstNonBlank(string? value, string? fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    internal static bool IsOutsideFocusedWord(string wordId, AgentToolContext context)
    {
        return !string.IsNullOrWhiteSpace(context.FocusedWordId)
            && !string.Equals(wordId, context.FocusedWordId, StringComparison.Ordinal);
    }

    internal static object FocusError(AgentToolContext context)
    {
        return new
        {
            error = $"This assistant session is focused on {context.FocusedWordLabel ?? "the current word"}. Use that word only for mutating changes.",
            focused_word_id = context.FocusedWordId,
        };
    }

    internal static object QuizContextRequired()
    {
        return new
        {
            error = "Choose a quiz before asking the assistant to inspect or change quiz content.",
        };
    }

    /// <summary>
    /// Refuses a mutation that would file content under the type the user did not ask for,
    /// or null when the request permits it.
    /// </summary>
    /// <remarks>
    /// The last line of defence for the sentences-stored-as-words failure. It has to sit at
    /// the execution boundary rather than in tool selection alone, because the offered tool
    /// list is not the only source of calls: a published agent declares its own tools, and a
    /// resumed chat replays a surface that no longer applies. Recoverable by design — nothing
    /// is queued and the model gets told which storage to use, so the existing tool loop can
    /// simply try again.
    /// </remarks>
    internal static object? WrongContentKind(AgentToolContext context, AssistantContentKind storing)
    {
        var requested = context.RequestedContentKind;
        if (requested == AssistantContentKind.Auto
            || requested == AssistantContentKind.Both
            || requested == storing)
        {
            return null;
        }

        return storing == AssistantContentKind.Words
            ? new { error = "The user asked for sentences. Use add_sentence or add_sentences, not word storage." }
            : new { error = "The user asked for words. Use add_word or add_words, not sentence storage." };
    }

    /// <summary>
    /// The comparison key for deciding whether two proposed strings are the same content.
    /// </summary>
    /// <remarks>
    /// Used only to detect the same text sent as both a word and a sentence in one proposal.
    /// Trailing sentence punctuation and repeated inner whitespace are ignored, because a
    /// model that repeats itself across two fields rarely repeats itself character for
    /// character. This never inspects shape: a string is only ever dropped because an actual
    /// proposed sentence matches it, so multiword vocabulary is unaffected.
    /// </remarks>
    internal static string NormalizeForDuplicateMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var collapsed = Regex.Replace(value.Trim(), @"\s+", " ");
        return collapsed.TrimEnd('.', '!', '?', '…', ' ');
    }

    internal static bool ContainsWord(string sentence, string word)
    {
        if (string.IsNullOrWhiteSpace(sentence) || string.IsNullOrWhiteSpace(word))
        {
            return false;
        }

        var pattern = $@"(?<![\p{{L}}\p{{M}}]){Regex.Escape(word.Trim())}(?![\p{{L}}\p{{M}}])";
        return Regex.IsMatch(sentence, pattern, RegexOptions.IgnoreCase);
    }

    internal static NullableGuidString GetNullableGuidString(JsonElement element, string property)
    {
        var value = GetString(element, property);
        if (string.IsNullOrWhiteSpace(value))
        {
            return new NullableGuidString(null, false);
        }

        return Guid.TryParse(value, out var parsed)
            ? new NullableGuidString(parsed, false)
            : new NullableGuidString(null, true);
    }

    internal static (IReadOnlyList<WordDraft> Words, IReadOnlyList<SkippedItem> Skipped) GetWordDrafts(
        JsonElement element,
        string property)
    {
        if (!element.TryGetProperty(property, out var wordsElement)
            || wordsElement.ValueKind != JsonValueKind.Array)
        {
            return ([], []);
        }

        var words = new List<WordDraft>();
        var skipped = new List<SkippedItem>();
        var index = 0;
        foreach (var item in wordsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                skipped.Add(new SkippedItem(index, "Each item must be an object with word and translation."));
                index++;
                continue;
            }

            var word = GetString(item, "word");
            var translation = GetString(item, "translation");
            if (string.IsNullOrWhiteSpace(word) || string.IsNullOrWhiteSpace(translation))
            {
                skipped.Add(new SkippedItem(index, "word and translation are both required."));
                index++;
                continue;
            }

            words.Add(new WordDraft(word.Trim(), translation.Trim()));
            index++;
        }

        return (words, skipped);
    }

    internal static (IReadOnlyList<WordEditDraft> Changes, IReadOnlyList<SkippedItem> Skipped) GetWordEditDrafts(
        JsonElement element,
        string property)
    {
        if (!element.TryGetProperty(property, out var changesElement)
            || changesElement.ValueKind != JsonValueKind.Array)
        {
            return ([], []);
        }

        var changes = new List<WordEditDraft>();
        var skipped = new List<SkippedItem>();
        var index = 0;
        foreach (var item in changesElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                skipped.Add(new SkippedItem(index, "Each item must be an object with word_id and a new word and/or translation."));
                index++;
                continue;
            }

            var wordId = GetString(item, "word_id");
            var word = GetString(item, "word")?.Trim();
            var translation = GetString(item, "translation")?.Trim();
            if (string.IsNullOrWhiteSpace(wordId))
            {
                skipped.Add(new SkippedItem(index, "word_id is required."));
                index++;
                continue;
            }
            if (string.IsNullOrWhiteSpace(word) && string.IsNullOrWhiteSpace(translation))
            {
                skipped.Add(new SkippedItem(index, "A new word and/or translation is required."));
                index++;
                continue;
            }

            changes.Add(new WordEditDraft(
                wordId.Trim(),
                string.IsNullOrWhiteSpace(word) ? null : word,
                string.IsNullOrWhiteSpace(translation) ? null : translation));
            index++;
        }

        return (changes, skipped);
    }

    internal static (IReadOnlyList<SentenceDraft> Sentences, IReadOnlyList<SkippedItem> Skipped) GetSentenceDrafts(
        JsonElement element,
        string property)
    {
        if (!element.TryGetProperty(property, out var sentencesElement)
            || sentencesElement.ValueKind != JsonValueKind.Array)
        {
            return ([], []);
        }

        var sentences = new List<SentenceDraft>();
        var skipped = new List<SkippedItem>();
        var index = 0;
        foreach (var item in sentencesElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                skipped.Add(new SkippedItem(index, "Each item must be an object with text and translation."));
                index++;
                continue;
            }

            var text = GetString(item, "text");
            var translation = GetString(item, "translation");
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(translation))
            {
                skipped.Add(new SkippedItem(index, "text and translation are both required."));
                index++;
                continue;
            }

            sentences.Add(new SentenceDraft(text.Trim(), translation.Trim()));
            index++;
        }

        return (sentences, skipped);
    }

    internal static (IReadOnlyList<SentenceEditDraft> Changes, IReadOnlyList<SkippedItem> Skipped) GetSentenceEditDrafts(
        JsonElement element,
        string property)
    {
        if (!element.TryGetProperty(property, out var changesElement)
            || changesElement.ValueKind != JsonValueKind.Array)
        {
            return ([], []);
        }

        var changes = new List<SentenceEditDraft>();
        var skipped = new List<SkippedItem>();
        var index = 0;
        foreach (var item in changesElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                skipped.Add(new SkippedItem(index, "Each item must contain sentence_id and a new text and/or translation."));
                index++;
                continue;
            }

            var sentenceIdText = GetString(item, "sentence_id");
            var text = GetString(item, "text")?.Trim();
            var translation = GetString(item, "translation")?.Trim();
            if (!Guid.TryParse(sentenceIdText, out var sentenceId))
            {
                skipped.Add(new SkippedItem(index, "sentence_id must be a valid id."));
                index++;
                continue;
            }
            if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(translation))
            {
                skipped.Add(new SkippedItem(index, "A new text and/or translation is required."));
                index++;
                continue;
            }

            changes.Add(new SentenceEditDraft(
                index,
                sentenceId,
                string.IsNullOrWhiteSpace(text) ? null : text,
                string.IsNullOrWhiteSpace(translation) ? null : translation));
            index++;
        }

        return (changes, skipped);
    }

    internal readonly record struct NullableGuidString(Guid? Value, bool Invalid);
    internal sealed record WordDraft(string Word, string Translation);
    internal readonly record struct WordEditDraft(string WordId, string? Word, string? Translation);
    internal sealed record SentenceDraft(string Text, string Translation);
    internal readonly record struct SentenceEditDraft(
        int Index,
        Guid SentenceId,
        string? Text,
        string? Translation);
    internal sealed record SkippedItem(int Index, string Reason);

    internal static void QueueSentenceEdit(
        AgentToolContext context,
        Guid sentenceId,
        SentenceDraft original,
        string? text,
        string? translation)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.EditSentence,
            sentence_id = sentenceId,
            original_text = original.Text,
            original_translation = original.Translation,
            text,
            translation,
        }, JsonOptions);
        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.EditSentence, payload));
    }
}
