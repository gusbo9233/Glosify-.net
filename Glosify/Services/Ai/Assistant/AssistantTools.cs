using System.Text.Json;
using Glosify.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace Glosify.Services;

public sealed class AssistantTools : IAssistantTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly GlosifyContext _context;

    public AssistantTools(GlosifyContext context)
    {
        _context = context;
    }

    private static readonly AgentToolDeclaration ListWordsDeclaration = new(
        "list_words",
        "List every word in the current quiz with its word text, translation, and id. Use this to see what is already in the quiz before proposing changes.",
        BuildSchema(new Dictionary<string, object>()));

    private static readonly AgentToolDeclaration GetWordDeclaration = new(
        "get_word",
        "Get a single word's quiz data and any matching quiz sentence by its id.",
        BuildSchema(new Dictionary<string, object>
        {
            ["word_id"] = StringProp("Id of the word to fetch."),
        }, required: ["word_id"]));

    private static readonly AgentToolDeclaration AddWordDeclaration = new(
        "add_word",
        "Propose adding a new word or short phrase to the quiz. Do not include example sentences here; use add_sentence for standalone quiz sentences. The change is queued; it is only saved when the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["word"] = StringProp("Word or short phrase in the target language. Prefer the exact form the learner should practice."),
            ["translation"] = StringProp("Translation in the user's source language."),
        }, required: ["word", "translation"]));

    private static readonly AgentToolDeclaration AddWordsDeclaration = new(
        "add_words",
        "Propose adding multiple words or short phrases to the quiz in one tool call. Prefer this over repeated add_word calls when adding more than one word. Each change is queued; it is only saved when the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["words"] = WordArrayProp("Words or short phrases to add to the quiz."),
        }, required: ["words"]));

    private static readonly AgentToolDeclaration AddSentenceDeclaration = new(
        "add_sentence",
        "Propose adding a standalone quiz sentence. Use this only when the user asks for sentences or pasted text already contains natural full sentences. The change is queued; it is only saved when the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["text"] = StringProp("Natural full sentence in the target language. Do not include notes, glosses, slash alternatives, pronunciation hints, or fragments."),
            ["translation"] = StringProp("Natural translation in the user's source language."),
        }, required: ["text", "translation"]));

    private static readonly AgentToolDeclaration EditWordDeclaration = new(
        "edit_word",
        "Propose changing an existing word and/or translation. The change is queued until the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["word_id"] = StringProp("Id of the word to edit."),
            ["word"] = StringProp("Optional. New word or short phrase."),
            ["translation"] = StringProp("Optional. New translation."),
        }, required: ["word_id"]));

    private static readonly AgentToolDeclaration EditWordsDeclaration = new(
        "edit_words",
        "Propose changing multiple existing words and/or translations in one tool call. Prefer this over repeated edit_word calls when editing more than one word. Each change is queued until the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["changes"] = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["description"] = "Word edits to queue.",
                ["items"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["word_id"] = StringProp("Id of the word to edit."),
                        ["word"] = StringProp("Optional. New word or short phrase."),
                        ["translation"] = StringProp("Optional. New translation."),
                    },
                    ["required"] = new[] { "word_id" },
                },
            },
        }, required: ["changes"]));

    private static readonly AgentToolDeclaration DeleteWordDeclaration = new(
        "delete_word",
        "Propose removing a word from the quiz. Queued until the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["word_id"] = StringProp("Id of the word to delete."),
        }, required: ["word_id"]));

    private static readonly AgentToolDeclaration RepairSentenceDeclaration = new(
        "repair_sentence",
        "Propose replacing all occurrences of a quiz example sentence with a corrected natural full sentence. Queued until Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["original_text"] = StringProp("The current sentence text to replace."),
            ["new_text"] = StringProp("The corrected natural full sentence in the target language. Do not include notes, glosses, slash alternatives, or pronunciation hints."),
            ["new_translation"] = StringProp("The corrected natural translation in the source language."),
        }, required: ["original_text", "new_text", "new_translation"]));

    private static readonly AgentToolDeclaration ListCollectionsDeclaration = new(
        "list_collections",
        "List the user's collections for the current language. Use this before proposing nested collections or placing a quiz into an existing collection.",
        BuildSchema(new Dictionary<string, object>
        {
            ["language"] = StringProp("Optional language to filter collections by. Defaults to the current app language when available."),
        }));

    private static readonly AgentToolDeclaration ListQuizzesDeclaration = new(
        "list_quizzes",
        "List the user's quizzes. Use this to avoid proposing duplicate quiz names.",
        BuildSchema(new Dictionary<string, object>
        {
            ["language"] = StringProp("Optional target language to filter quizzes by. Defaults to the current app language when available."),
        }));

    private static readonly AgentToolDeclaration CreateCollectionDeclaration = new(
        "create_collection",
        "Propose creating a collection. The change is queued; it is only saved when the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["name"] = StringProp("Collection name."),
            ["language"] = StringProp("Collection language. Defaults to the current app language when available."),
            ["parent_collection_id"] = StringProp("Optional id of the parent collection."),
        }, required: ["name"]));

    private static readonly AgentToolDeclaration CreateQuizDeclaration = new(
        "create_quiz",
        "Propose creating a quiz. The change is queued; it is only saved when the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["name"] = StringProp("Quiz name."),
            ["source_language"] = StringProp("Language the user already knows."),
            ["target_language"] = StringProp("Language being learned. Defaults to the current app language when available."),
            ["collection_id"] = StringProp("Optional id of the collection that should contain the quiz."),
            ["words"] = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["description"] = "Optional starter vocabulary for the new quiz.",
                ["items"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["word"] = StringProp("Word or short phrase in the target language."),
                        ["translation"] = StringProp("Translation in the source language."),
                    },
                    ["required"] = new[] { "word", "translation" },
                },
            },
        }, required: ["name", "source_language"]));

    public IReadOnlyList<AgentToolDeclaration> Declarations { get; } =
    [
        ListWordsDeclaration,
        GetWordDeclaration,
        AddWordDeclaration,
        AddWordsDeclaration,
        AddSentenceDeclaration,
        EditWordDeclaration,
        EditWordsDeclaration,
        DeleteWordDeclaration,
        RepairSentenceDeclaration,
    ];

    public IReadOnlyList<AgentToolDeclaration> GlobalDeclarations { get; } =
    [
        ListCollectionsDeclaration,
        ListQuizzesDeclaration,
        CreateCollectionDeclaration,
        CreateQuizDeclaration,
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
            "list_collections" => await ListCollectionsAsync(args, context, cancellationToken),
            "list_quizzes" => await ListQuizzesAsync(args, context, cancellationToken),
            "add_word" => QueueAddWord(args, context),
            "add_words" => QueueAddWords(args, context),
            "add_sentence" => QueueAddSentence(args, context),
            "edit_word" => await QueueEditWordAsync(args, context, cancellationToken),
            "edit_words" => await QueueEditWordsAsync(args, context, cancellationToken),
            "delete_word" => QueueDeleteWord(args, context),
            "repair_sentence" => QueueRepairSentence(args, context),
            "create_quiz" => QueueCreateQuiz(args, context),
            "create_collection" => QueueCreateCollection(args, context),
            _ => new { error = $"Unknown tool: {name}" },
        };
    }

    private async Task<object> ListWordsAsync(AgentToolContext context, CancellationToken ct)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var rows = await _context.Words
            .Where(w => w.QuizId == context.QuizId.Value)
            .OrderBy(w => w.Lemma)
            .Select(w => new { id = w.Id, word = w.Lemma, translation = w.Translation })
            .ToListAsync(ct);
        return new { words = rows, count = rows.Count };
    }

    private async Task<object> GetWordAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var wordId = args.TryGetProperty("word_id", out var idProp) ? idProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(wordId))
        {
            return new { error = "word_id is required." };
        }

        var word = await _context.Words.FirstOrDefaultAsync(w => w.Id == wordId && w.QuizId == context.QuizId.Value, ct);
        if (word == null)
        {
            return new { error = $"Word {wordId} not found in this quiz." };
        }

        var sentence = await _context.QuizSentences
            .Where(s => s.QuizId == context.QuizId.Value)
            .ToListAsync(ct);
        var quizSentence = sentence.FirstOrDefault(s => ContainsWord(s.Text, word.Lemma));
        return new
        {
            id = word.Id,
            word = word.Lemma,
            translation = word.Translation,
            example_sentence = quizSentence?.Text ?? string.Empty,
            example_sentence_translation = quizSentence?.Translation ?? string.Empty,
        };
    }

    private async Task<object> ListCollectionsAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var language = GetString(args, "language") ?? context.CurrentLanguage;
        if (string.IsNullOrWhiteSpace(language))
        {
            return new { error = "language is required when no current app language is selected." };
        }

        var rows = await _context.Collections
            .Where(c => c.UserId == context.UserId && c.Language == language.Trim())
            .OrderBy(c => c.ParentCollectionId.HasValue)
            .ThenBy(c => c.Name)
            .Select(c => new
            {
                id = c.Id,
                name = c.Name,
                language = c.Language,
                parent_collection_id = c.ParentCollectionId,
            })
            .ToListAsync(ct);

        return new { collections = rows, count = rows.Count };
    }

    private async Task<object> ListQuizzesAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var language = GetString(args, "language") ?? context.CurrentLanguage;
        var query = _context.Quizzes.Where(q => q.UserId == context.UserId);
        if (!string.IsNullOrWhiteSpace(language))
        {
            var trimmed = language.Trim();
            query = query.Where(q => q.TargetLanguage == trimmed || q.Language == trimmed);
        }

        var rows = await query
            .OrderBy(q => q.Name)
            .Select(q => new
            {
                id = q.Id,
                name = q.Name,
                source_language = q.SourceLanguage,
                target_language = q.TargetLanguage,
                collection_id = q.CollectionId,
            })
            .ToListAsync(ct);

        return new { quizzes = rows, count = rows.Count };
    }

    private static object QueueAddWord(JsonElement args, AgentToolContext context)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var word = GetString(args, "word") ?? GetString(args, "lemma");
        var translation = GetString(args, "translation");
        if (string.IsNullOrWhiteSpace(word) || string.IsNullOrWhiteSpace(translation))
        {
            return new { error = "word and translation are required." };
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.AddWord,
            word = word.Trim(),
            translation = translation.Trim(),
        }, JsonOptions);

        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.AddWord, payload));
        return new { queued = true, kind = PendingChangeKinds.AddWord, word };
    }

    private static object QueueAddWords(JsonElement args, AgentToolContext context)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var words = GetWordDrafts(args, "words");
        if (words.Count == 0)
        {
            return new { error = "At least one valid word and translation is required." };
        }

        foreach (var word in words)
        {
            var payload = JsonSerializer.SerializeToElement(new
            {
                kind = PendingChangeKinds.AddWord,
                word = word.Word,
                translation = word.Translation,
            }, JsonOptions);
            context.PendingChanges.Add(new PendingChange(PendingChangeKinds.AddWord, payload));
        }

        return new { queued = true, kind = "add_words", count = words.Count };
    }

    private static object QueueAddSentence(JsonElement args, AgentToolContext context)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var text = GetString(args, "text") ?? GetString(args, "sentence");
        var translation = GetString(args, "translation");
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(translation))
        {
            return new { error = "text and translation are required." };
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.AddSentence,
            text = text.Trim(),
            translation = translation.Trim(),
        }, JsonOptions);

        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.AddSentence, payload));
        return new { queued = true, kind = PendingChangeKinds.AddSentence };
    }

    private async Task<object> QueueEditWordAsync(JsonElement args, AgentToolContext context, CancellationToken cancellationToken)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var wordId = GetString(args, "word_id");
        if (string.IsNullOrWhiteSpace(wordId))
        {
            return new { error = "word_id is required." };
        }
        if (IsOutsideFocusedWord(wordId, context))
        {
            return FocusError(context);
        }

        var original = await LoadOriginalWordAsync(context.QuizId.Value, wordId, cancellationToken);
        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.EditWord,
            word_id = wordId,
            original_word = original?.Word,
            original_translation = original?.Translation,
            word = (GetString(args, "word") ?? GetString(args, "lemma"))?.Trim(),
            translation = GetString(args, "translation")?.Trim(),
        }, JsonOptions);

        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.EditWord, payload));
        return new { queued = true, kind = PendingChangeKinds.EditWord, word_id = wordId };
    }

    private async Task<object> QueueEditWordsAsync(JsonElement args, AgentToolContext context, CancellationToken cancellationToken)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var changes = GetWordEditDrafts(args, "changes");
        if (changes.Count == 0)
        {
            return new { error = "At least one valid word edit is required." };
        }

        var outsideFocus = changes.FirstOrDefault(change => IsOutsideFocusedWord(change.WordId, context));
        if (outsideFocus.WordId is not null)
        {
            return FocusError(context);
        }

        var originals = await LoadOriginalWordsAsync(
            context.QuizId.Value,
            changes.Select(change => change.WordId).ToList(),
            cancellationToken);
        foreach (var change in changes)
        {
            originals.TryGetValue(change.WordId, out var original);
            var payload = JsonSerializer.SerializeToElement(new
            {
                kind = PendingChangeKinds.EditWord,
                word_id = change.WordId,
                original_word = original?.Word,
                original_translation = original?.Translation,
                word = change.Word,
                translation = change.Translation,
            }, JsonOptions);
            context.PendingChanges.Add(new PendingChange(PendingChangeKinds.EditWord, payload));
        }

        return new { queued = true, kind = "edit_words", count = changes.Count };
    }

    private async Task<WordDraft?> LoadOriginalWordAsync(Guid quizId, string wordId, CancellationToken cancellationToken)
    {
        if (_context == null)
        {
            return null;
        }

        var word = await _context.Words
            .Where(row => row.QuizId == quizId && row.Id == wordId)
            .Select(row => new WordDraft(row.Lemma, row.Translation))
            .FirstOrDefaultAsync(cancellationToken);
        return word;
    }

    private async Task<Dictionary<string, WordDraft>> LoadOriginalWordsAsync(
        Guid quizId,
        IReadOnlyList<string> wordIds,
        CancellationToken cancellationToken)
    {
        if (_context == null || wordIds.Count == 0)
        {
            return [];
        }

        return await _context.Words
            .Where(row => row.QuizId == quizId && wordIds.Contains(row.Id))
            .Select(row => new { row.Id, row.Lemma, row.Translation })
            .ToDictionaryAsync(
                row => row.Id,
                row => new WordDraft(row.Lemma, row.Translation),
                cancellationToken);
    }

    private static object QueueDeleteWord(JsonElement args, AgentToolContext context)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

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

    private static object QueueRepairSentence(JsonElement args, AgentToolContext context)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

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

    private static object QueueCreateQuiz(JsonElement args, AgentToolContext context)
    {
        var name = GetString(args, "name");
        var sourceLanguage = GetString(args, "source_language");
        var targetLanguage = GetString(args, "target_language") ?? context.CurrentLanguage;
        var collectionId = GetNullableGuidString(args, "collection_id");
        var words = GetWordDrafts(args, "words");

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(sourceLanguage))
        {
            return new { error = "name and source_language are required." };
        }

        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            return new { error = "target_language is required when no current app language is selected." };
        }

        if (collectionId.Invalid)
        {
            return new { error = "collection_id must be a valid id." };
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.CreateQuiz,
            name = name.Trim(),
            source_language = sourceLanguage.Trim(),
            target_language = targetLanguage.Trim(),
            collection_id = collectionId.Value,
            words,
        }, JsonOptions);

        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.CreateQuiz, payload));
        return new { queued = true, kind = PendingChangeKinds.CreateQuiz, name = name.Trim() };
    }

    private static object QueueCreateCollection(JsonElement args, AgentToolContext context)
    {
        var name = GetString(args, "name");
        var language = GetString(args, "language") ?? context.CurrentLanguage;
        var parentCollectionId = GetNullableGuidString(args, "parent_collection_id");

        if (string.IsNullOrWhiteSpace(name))
        {
            return new { error = "name is required." };
        }

        if (string.IsNullOrWhiteSpace(language))
        {
            return new { error = "language is required when no current app language is selected." };
        }

        if (parentCollectionId.Invalid)
        {
            return new { error = "parent_collection_id must be a valid id." };
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.CreateCollection,
            name = name.Trim(),
            language = language.Trim(),
            parent_collection_id = parentCollectionId.Value,
        }, JsonOptions);

        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.CreateCollection, payload));
        return new { queued = true, kind = PendingChangeKinds.CreateCollection, name = name.Trim() };
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

    private static object QuizContextRequired()
    {
        return new
        {
            error = "Choose a quiz before asking the assistant to inspect or change quiz content.",
        };
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

    private static NullableGuidString GetNullableGuidString(JsonElement element, string property)
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

    private static IReadOnlyList<WordDraft> GetWordDrafts(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var wordsElement)
            || wordsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var words = new List<WordDraft>();
        foreach (var item in wordsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var word = GetString(item, "word") ?? GetString(item, "lemma");
            var translation = GetString(item, "translation");
            if (string.IsNullOrWhiteSpace(word) || string.IsNullOrWhiteSpace(translation))
            {
                continue;
            }

            words.Add(new WordDraft(word.Trim(), translation.Trim()));
        }

        return words;
    }

    private static IReadOnlyList<WordEditDraft> GetWordEditDrafts(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var changesElement)
            || changesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var changes = new List<WordEditDraft>();
        foreach (var item in changesElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var wordId = GetString(item, "word_id");
            var word = (GetString(item, "word") ?? GetString(item, "lemma"))?.Trim();
            var translation = GetString(item, "translation")?.Trim();
            if (string.IsNullOrWhiteSpace(wordId)
                || (string.IsNullOrWhiteSpace(word) && string.IsNullOrWhiteSpace(translation)))
            {
                continue;
            }

            changes.Add(new WordEditDraft(
                wordId.Trim(),
                string.IsNullOrWhiteSpace(word) ? null : word,
                string.IsNullOrWhiteSpace(translation) ? null : translation));
        }

        return changes;
    }

    private static object WordArrayProp(string description) =>
        new Dictionary<string, object>
        {
            ["type"] = "array",
            ["description"] = description,
            ["items"] = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["word"] = StringProp("Word or short phrase in the target language."),
                    ["translation"] = StringProp("Translation in the source language."),
                },
                ["required"] = new[] { "word", "translation" },
            },
        };

    private readonly record struct NullableGuidString(Guid? Value, bool Invalid);
    private sealed record WordDraft(string Word, string Translation);
    private readonly record struct WordEditDraft(string WordId, string? Word, string? Translation);

}
