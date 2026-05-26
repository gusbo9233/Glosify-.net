using System.Text.Json;
using Glosify.Data;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services;

public sealed class AssistantTools : IAssistantTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly GlosifyContext _context;

    public AssistantTools(GlosifyContext context)
    {
        _context = context;
    }

    public IReadOnlyList<AgentToolDeclaration> Declarations { get; } =
    [
        new(
            "list_words",
            "List every word in the current quiz with its lemma, translation, and id. Use this to see what is already in the quiz before proposing changes.",
            BuildSchema(new Dictionary<string, object>())),

        new(
            "get_word",
            "Get a single word's full detail (translation, example sentence, explanation, properties, variants) by its id.",
            BuildSchema(new Dictionary<string, object>
            {
                ["word_id"] = StringProp("Id of the word to fetch."),
            }, required: ["word_id"])),

        new(
            "add_word",
            "Propose adding a new word to the quiz. Include a natural full example sentence and translation when possible. The change is queued; it is only saved when the user clicks Apply.",
            BuildSchema(new Dictionary<string, object>
            {
                ["lemma"] = StringProp("Dictionary form of the word in the target language."),
                ["translation"] = StringProp("Translation in the user's source language."),
                ["example_sentence"] = StringProp("Optional. Natural full sentence in the target language using the word's lemma or an inflected form. Do not include notes, glosses, slash alternatives, or pronunciation hints."),
                ["example_sentence_translation"] = StringProp("Optional. Natural source-language translation of the example sentence."),
            }, required: ["lemma", "translation"])),

        new(
            "edit_word",
            "Propose changing an existing word's lemma and/or translation. The change is queued until the user clicks Apply.",
            BuildSchema(new Dictionary<string, object>
            {
                ["word_id"] = StringProp("Id of the word to edit."),
                ["lemma"] = StringProp("Optional. New lemma."),
                ["translation"] = StringProp("Optional. New translation."),
            }, required: ["word_id"])),

        new(
            "delete_word",
            "Propose removing a word from the quiz. Queued until the user clicks Apply.",
            BuildSchema(new Dictionary<string, object>
            {
                ["word_id"] = StringProp("Id of the word to delete."),
            }, required: ["word_id"])),

        new(
            "set_word_detail",
            "Propose updating the grammar details, explanation, and/or example sentence of a word. Use this when the user asks to generate variants, properties, grammar forms, explanations, or sentences for existing quiz words. Queued until the user clicks Apply.",
            BuildSchema(new Dictionary<string, object>
            {
                ["word_id"] = StringProp("Id of the word whose detail to update."),
                ["properties"] = ObjectProp("Optional. Grammar property map, such as {\"pos\":\"noun\",\"gender\":\"neuter\"}. Use snake_case keys; keep values concise."),
                ["variants"] = VariantsProp("Optional. Inflected forms to show on the word detail page. Each variant needs a form plus the label/group the user should see, such as group \"Past Plural\" and label \"1st masculine personal\". Tags are optional metadata for compatibility."),
                ["explanation"] = StringProp("Optional. New short explanation in the user's source language."),
                ["example_sentence"] = StringProp("Optional. Natural full sentence in the target language using this word's lemma or an inflected form. Do not include notes, glosses, slash alternatives, or pronunciation hints."),
                ["example_sentence_translation"] = StringProp("Optional. Natural source-language translation of the new example sentence."),
            }, required: ["word_id"])),

        new(
            "repair_sentence",
            "Propose replacing all occurrences of an example sentence (across word_details) with a corrected natural full sentence. Queued until Apply.",
            BuildSchema(new Dictionary<string, object>
            {
                ["original_text"] = StringProp("The current sentence text to replace."),
                ["new_text"] = StringProp("The corrected natural full sentence in the target language. Do not include notes, glosses, slash alternatives, or pronunciation hints."),
                ["new_translation"] = StringProp("The corrected natural translation in the source language."),
            }, required: ["original_text", "new_text", "new_translation"])),
    ];

    public async Task<object> ExecuteAsync(
        string name,
        string argsJson,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        var args = ParseArgs(argsJson);

        return name switch
        {
            "list_words" => await ListWordsAsync(context, cancellationToken),
            "get_word" => await GetWordAsync(args, context, cancellationToken),
            "add_word" => QueueAddWord(args, context),
            "edit_word" => QueueEditWord(args, context),
            "delete_word" => QueueDeleteWord(args, context),
            "set_word_detail" => QueueSetWordDetail(args, context),
            "repair_sentence" => QueueRepairSentence(args, context),
            _ => new { error = $"Unknown tool: {name}" },
        };
    }

    private async Task<object> ListWordsAsync(AgentToolContext context, CancellationToken ct)
    {
        var rows = await _context.Words
            .Where(w => w.QuizId == context.QuizId)
            .OrderBy(w => w.Lemma)
            .Select(w => new { id = w.Id, lemma = w.Lemma, translation = w.Translation, word_detail_id = w.WordDetailId })
            .ToListAsync(ct);
        return new { words = rows, count = rows.Count };
    }

    private async Task<object> GetWordAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var wordId = args.TryGetProperty("word_id", out var idProp) ? idProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(wordId))
        {
            return new { error = "word_id is required." };
        }

        var word = await _context.Words.FirstOrDefaultAsync(w => w.Id == wordId && w.QuizId == context.QuizId, ct);
        if (word == null)
        {
            return new { error = $"Word {wordId} not found in this quiz." };
        }

        var detail = await _context.WordDetails.FirstOrDefaultAsync(d => d.Id == word.WordDetailId, ct);
        return new
        {
            id = word.Id,
            lemma = word.Lemma,
            translation = word.Translation,
            explanation = detail?.Explanation ?? string.Empty,
            example_sentence = detail?.ExampleSentence ?? string.Empty,
            example_sentence_translation = detail?.ExampleSentenceTranslation ?? string.Empty,
            properties_json = detail?.Properties ?? "{}",
            variants_json = detail?.Variants ?? "[]",
        };
    }

    private static object QueueAddWord(JsonElement args, AgentToolContext context)
    {
        var lemma = GetString(args, "lemma");
        var translation = GetString(args, "translation");
        if (string.IsNullOrWhiteSpace(lemma) || string.IsNullOrWhiteSpace(translation))
        {
            return new { error = "lemma and translation are required." };
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.AddWord,
            lemma = lemma.Trim(),
            translation = translation.Trim(),
            example_sentence = GetString(args, "example_sentence")?.Trim() ?? string.Empty,
            example_sentence_translation = GetString(args, "example_sentence_translation")?.Trim() ?? string.Empty,
        }, JsonOptions);

        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.AddWord, payload));
        return new { queued = true, kind = PendingChangeKinds.AddWord, lemma };
    }

    private static object QueueEditWord(JsonElement args, AgentToolContext context)
    {
        var wordId = GetString(args, "word_id");
        if (string.IsNullOrWhiteSpace(wordId))
        {
            return new { error = "word_id is required." };
        }
        if (IsOutsideFocusedWord(wordId, context))
        {
            return FocusError(context);
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.EditWord,
            word_id = wordId,
            lemma = GetString(args, "lemma")?.Trim(),
            translation = GetString(args, "translation")?.Trim(),
        }, JsonOptions);

        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.EditWord, payload));
        return new { queued = true, kind = PendingChangeKinds.EditWord, word_id = wordId };
    }

    private static object QueueDeleteWord(JsonElement args, AgentToolContext context)
    {
        var wordId = GetString(args, "word_id");
        if (string.IsNullOrWhiteSpace(wordId))
        {
            return new { error = "word_id is required." };
        }
        if (IsOutsideFocusedWord(wordId, context))
        {
            return FocusError(context);
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.DeleteWord,
            word_id = wordId,
        }, JsonOptions);

        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.DeleteWord, payload));
        return new { queued = true, kind = PendingChangeKinds.DeleteWord, word_id = wordId };
    }

    private static object QueueSetWordDetail(JsonElement args, AgentToolContext context)
    {
        var wordId = GetString(args, "word_id");
        if (string.IsNullOrWhiteSpace(wordId))
        {
            return new { error = "word_id is required." };
        }
        if (IsOutsideFocusedWord(wordId, context))
        {
            return FocusError(context);
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.SetWordDetail,
            word_id = wordId,
            properties = GetElementOrNull(args, "properties"),
            variants = GetElementOrNull(args, "variants"),
            explanation = GetString(args, "explanation")?.Trim(),
            example_sentence = GetString(args, "example_sentence")?.Trim(),
            example_sentence_translation = GetString(args, "example_sentence_translation")?.Trim(),
        }, JsonOptions);

        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.SetWordDetail, payload));
        return new { queued = true, kind = PendingChangeKinds.SetWordDetail, word_id = wordId };
    }

    private static object QueueRepairSentence(JsonElement args, AgentToolContext context)
    {
        var original = GetString(args, "original_text");
        var newText = GetString(args, "new_text");
        var newTranslation = GetString(args, "new_translation");
        if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(newText) || string.IsNullOrWhiteSpace(newTranslation))
        {
            return new { error = "original_text, new_text, and new_translation are all required." };
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.RepairSentence,
            word_id = context.FocusedWordId,
            original_text = original.Trim(),
            new_text = newText.Trim(),
            new_translation = newTranslation.Trim(),
        }, JsonOptions);

        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.RepairSentence, payload));
        return new { queued = true, kind = PendingChangeKinds.RepairSentence };
    }

    private static JsonElement ParseArgs(string argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
        {
            return JsonDocument.Parse("{}").RootElement;
        }
        try
        {
            return JsonDocument.Parse(argsJson).RootElement;
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}").RootElement;
        }
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static JsonElement? GetElementOrNull(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : value.Clone();
    }

    private static bool IsOutsideFocusedWord(string wordId, AgentToolContext context)
    {
        return !string.IsNullOrWhiteSpace(context.FocusedWordId)
            && !string.Equals(wordId, context.FocusedWordId, StringComparison.Ordinal);
    }

    private static object FocusError(AgentToolContext context)
    {
        return new
        {
            error = $"This assistant session is focused on {context.FocusedWordLabel ?? "the current word"}. Use that word only for mutating changes.",
            focused_word_id = context.FocusedWordId,
        };
    }

    private static object BuildSchema(Dictionary<string, object> properties, IReadOnlyList<string>? required = null)
    {
        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (required is { Count: > 0 })
        {
            schema["required"] = required;
        }
        return schema;
    }

    private static object StringProp(string description) =>
        new Dictionary<string, object>
        {
            ["type"] = "string",
            ["description"] = description,
        };

    private static object ObjectProp(string description) =>
        new Dictionary<string, object>
        {
            ["type"] = "object",
            ["description"] = description,
            ["additionalProperties"] = true,
        };

    private static object VariantsProp(string description) =>
        new Dictionary<string, object>
        {
            ["type"] = "array",
            ["description"] = description,
            ["items"] = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["form"] = StringProp("Inflected form."),
                    ["label"] = StringProp("Display label for this form, for example \"1st masculine personal\"."),
                    ["group"] = StringProp("Optional display group, for example \"Past Plural\" or \"Imperative\"."),
                    ["tags"] = new Dictionary<string, object>
                    {
                        ["type"] = "array",
                        ["items"] = StringProp("Single grammar tag."),
                    },
                },
                ["required"] = new[] { "form" },
            },
        };
}
