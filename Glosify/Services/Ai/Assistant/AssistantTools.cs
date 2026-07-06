using System.Text.Json;
using Glosify.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using Glosify.Services.Ai.Llm;

namespace Glosify.Services.Ai.Assistant;

public sealed class AssistantTools : IAssistantTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly GlosifyContext _context;

    public AssistantTools(GlosifyContext context)
    {
        _context = context;
    }

    private const int ListPageSize = 200;

    private static readonly AgentToolDeclaration ListWordsDeclaration = new(
        "list_words",
        "List the words in the current quiz with word text, translation, and id. Use this to see what is already in the quiz before proposing changes. Returns up to 200 words per call; when has_more is true, call again with the next offset.",
        BuildSchema(new Dictionary<string, object>
        {
            ["offset"] = new Dictionary<string, object>
            {
                ["type"] = "integer",
                ["description"] = "Optional. Number of words to skip, for paging. Defaults to 0.",
            },
        }));

    private static readonly AgentToolDeclaration ListSentencesDeclaration = new(
        "list_sentences",
        "List the standalone quiz sentences in the current quiz with text, translation, and id. Use this before repairing or deleting sentences; repair_sentence must match the existing sentence text exactly. Returns up to 200 sentences per call; when has_more is true, call again with the next offset.",
        BuildSchema(new Dictionary<string, object>
        {
            ["offset"] = new Dictionary<string, object>
            {
                ["type"] = "integer",
                ["description"] = "Optional. Number of sentences to skip, for paging. Defaults to 0.",
            },
        }));

    private static readonly AgentToolDeclaration GetWordDeclaration = new(
        "get_word",
        "Get a single word's quiz data and any matching quiz sentence by its id.",
        BuildSchema(new Dictionary<string, object>
        {
            ["word_id"] = StringProp("Id of the word to fetch."),
        }, required: ["word_id"]));

    private static readonly AgentToolDeclaration SearchWordsDeclaration = new(
        "search_words",
        "Search the current quiz's word text and translations. Use this instead of paging through the full quiz when looking for a specific word or meaning.",
        BuildSchema(new Dictionary<string, object>
        {
            ["query"] = StringProp("Text to search for in either the target-language word or its translation."),
            ["limit"] = IntegerProp("Optional maximum number of matches to return, from 1 to 50. Defaults to 20."),
        }, required: ["query"]));

    private static readonly AgentToolDeclaration GetQuizSummaryDeclaration = new(
        "get_quiz_summary",
        "Get the current quiz's name, languages, collection, visibility, and word and sentence counts.",
        BuildSchema([]));

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

    private static readonly AgentToolDeclaration AddSentencesDeclaration = new(
        "add_sentences",
        "Propose adding multiple standalone quiz sentences in one tool call. Prefer this over repeated add_sentence calls. Each change is queued until the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["sentences"] = SentenceArrayProp("Natural full sentences to add to the quiz."),
        }, required: ["sentences"]));

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

    private static readonly AgentToolDeclaration EditSentenceDeclaration = new(
        "edit_sentence",
        "Propose changing an existing standalone quiz sentence and/or its translation by id. The change is queued until the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["sentence_id"] = StringProp("Id of the sentence to edit. Use list_sentences to find it."),
            ["text"] = StringProp("Optional new natural full sentence in the target language."),
            ["translation"] = StringProp("Optional new translation in the source language."),
        }, required: ["sentence_id"]));

    private static readonly AgentToolDeclaration EditSentencesDeclaration = new(
        "edit_sentences",
        "Propose changing multiple existing standalone quiz sentences and/or translations by id. Prefer this over repeated edit_sentence calls. Each change is queued until the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["changes"] = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["description"] = "Sentence edits to queue.",
                ["items"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["sentence_id"] = StringProp("Id of the sentence to edit."),
                        ["text"] = StringProp("Optional new natural full sentence in the target language."),
                        ["translation"] = StringProp("Optional new translation in the source language."),
                    },
                    ["required"] = new[] { "sentence_id" },
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

    private static readonly AgentToolDeclaration DeleteSentenceDeclaration = new(
        "delete_sentence",
        "Propose removing a standalone quiz sentence by its id. Use list_sentences to find the id. Queued until the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["sentence_id"] = StringProp("Id of the sentence to delete."),
        }, required: ["sentence_id"]));

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

    private static readonly AgentToolDeclaration MoveQuizDeclaration = new(
        "move_quiz",
        "Propose moving one of the user's quizzes into a collection. Omit collection_id to move it to the library root. The change is queued until the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["quiz_id"] = StringProp("Id of the quiz to move. Use list_quizzes to find it."),
            ["collection_id"] = StringProp("Optional destination collection id. Omit to move the quiz to the library root."),
        }, required: ["quiz_id"]));

    private static readonly AgentToolDeclaration RenameCollectionDeclaration = new(
        "rename_collection",
        "Propose renaming one of the user's collections. The change is queued until the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["collection_id"] = StringProp("Id of the collection to rename. Use list_collections to find it."),
            ["name"] = StringProp("New collection name."),
        }, required: ["collection_id", "name"]));

    private static readonly AgentToolDeclaration MoveCollectionDeclaration = new(
        "move_collection",
        "Propose moving a collection under another collection. Omit parent_collection_id to move it to the library root. The change is queued until the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["collection_id"] = StringProp("Id of the collection to move. Use list_collections to find it."),
            ["parent_collection_id"] = StringProp("Optional destination parent collection id. Omit to move the collection to the library root."),
        }, required: ["collection_id"]));

    public IReadOnlyList<AgentToolDeclaration> Declarations { get; } =
    [
        ListWordsDeclaration,
        SearchWordsDeclaration,
        GetWordDeclaration,
        GetQuizSummaryDeclaration,
        ListSentencesDeclaration,
        AddWordDeclaration,
        AddWordsDeclaration,
        AddSentenceDeclaration,
        AddSentencesDeclaration,
        EditWordDeclaration,
        EditWordsDeclaration,
        EditSentenceDeclaration,
        EditSentencesDeclaration,
        DeleteWordDeclaration,
        RepairSentenceDeclaration,
        DeleteSentenceDeclaration,
    ];

    public IReadOnlyList<AgentToolDeclaration> GlobalDeclarations { get; } =
    [
        ListCollectionsDeclaration,
        ListQuizzesDeclaration,
        CreateCollectionDeclaration,
        CreateQuizDeclaration,
        MoveQuizDeclaration,
        RenameCollectionDeclaration,
        MoveCollectionDeclaration,
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
            "list_words" => await ListWordsAsync(args, context, cancellationToken),
            "search_words" => await SearchWordsAsync(args, context, cancellationToken),
            "get_word" => await GetWordAsync(args, context, cancellationToken),
            "get_quiz_summary" => await GetQuizSummaryAsync(context, cancellationToken),
            "list_sentences" => await ListSentencesAsync(args, context, cancellationToken),
            "list_collections" => await ListCollectionsAsync(args, context, cancellationToken),
            "list_quizzes" => await ListQuizzesAsync(args, context, cancellationToken),
            "add_word" => QueueAddWord(args, context),
            "add_words" => QueueAddWords(args, context),
            "add_sentence" => QueueAddSentence(args, context),
            "add_sentences" => QueueAddSentences(args, context),
            "edit_word" => await QueueEditWordAsync(args, context, cancellationToken),
            "edit_words" => await QueueEditWordsAsync(args, context, cancellationToken),
            "edit_sentence" => await QueueEditSentenceAsync(args, context, cancellationToken),
            "edit_sentences" => await QueueEditSentencesAsync(args, context, cancellationToken),
            "delete_word" => QueueDeleteWord(args, context),
            "repair_sentence" => QueueRepairSentence(args, context),
            "delete_sentence" => await QueueDeleteSentenceAsync(args, context, cancellationToken),
            "create_quiz" => QueueCreateQuiz(args, context),
            "create_collection" => QueueCreateCollection(args, context),
            "move_quiz" => await QueueMoveQuizAsync(args, context, cancellationToken),
            "rename_collection" => await QueueRenameCollectionAsync(args, context, cancellationToken),
            "move_collection" => await QueueMoveCollectionAsync(args, context, cancellationToken),
            _ => new { error = $"Unknown tool: {name}" },
        };
    }

    private async Task<object> ListWordsAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var offset = GetOffset(args);
        var query = _context.Words.Where(w => w.QuizId == context.QuizId.Value);
        var totalCount = await query.CountAsync(ct);
        var rows = await query
            .OrderBy(w => w.Lemma)
            .Skip(offset)
            .Take(ListPageSize)
            .Select(w => new { id = w.Id, word = w.Lemma, translation = w.Translation })
            .ToListAsync(ct);

        return new
        {
            words = rows,
            total_count = totalCount,
            offset,
            has_more = offset + rows.Count < totalCount,
        };
    }

    private async Task<object> SearchWordsAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var search = GetString(args, "query")?.Trim();
        if (string.IsNullOrWhiteSpace(search))
        {
            return new { error = "query is required." };
        }

        var ownsQuiz = await _context.Quizzes
            .AnyAsync(q => q.Id == context.QuizId.Value && q.UserId == context.UserId, ct);
        if (!ownsQuiz)
        {
            return QuizContextRequired();
        }

        var normalized = search.ToLowerInvariant();
        var limit = GetBoundedInt(args, "limit", defaultValue: 20, min: 1, max: 50);
        var query = _context.Words
            .Where(w => w.QuizId == context.QuizId.Value
                && (w.Lemma.ToLower().Contains(normalized)
                    || w.Translation.ToLower().Contains(normalized)));
        var totalCount = await query.CountAsync(ct);
        var rows = await query
            .OrderBy(w => w.Lemma)
            .Take(limit)
            .Select(w => new { id = w.Id, word = w.Lemma, translation = w.Translation })
            .ToListAsync(ct);

        return new
        {
            query = search,
            words = rows,
            total_count = totalCount,
            returned_count = rows.Count,
            has_more = rows.Count < totalCount,
        };
    }

    private async Task<object> GetQuizSummaryAsync(AgentToolContext context, CancellationToken ct)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var quiz = await _context.Quizzes
            .Where(q => q.Id == context.QuizId.Value && q.UserId == context.UserId)
            .Select(q => new
            {
                q.Id,
                q.Name,
                q.SourceLanguage,
                q.TargetLanguage,
                q.IsPublic,
                q.CreatedAt,
                q.CollectionId,
                CollectionName = q.Collection == null ? null : q.Collection.Name,
            })
            .FirstOrDefaultAsync(ct);
        if (quiz == null)
        {
            return QuizContextRequired();
        }

        var wordCount = await _context.Words.CountAsync(w => w.QuizId == quiz.Id, ct);
        var sentenceCount = await _context.QuizSentences.CountAsync(s => s.QuizId == quiz.Id, ct);

        return new
        {
            id = quiz.Id,
            name = quiz.Name,
            source_language = quiz.SourceLanguage,
            target_language = quiz.TargetLanguage,
            is_public = quiz.IsPublic,
            created_at = quiz.CreatedAt,
            collection_id = quiz.CollectionId,
            collection_name = quiz.CollectionName,
            word_count = wordCount,
            sentence_count = sentenceCount,
        };
    }

    private async Task<object> ListSentencesAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var offset = GetOffset(args);
        var query = _context.QuizSentences.Where(s => s.QuizId == context.QuizId.Value);
        var totalCount = await query.CountAsync(ct);
        var rows = await query
            .OrderBy(s => s.CreatedAt)
            .Skip(offset)
            .Take(ListPageSize)
            .Select(s => new { id = s.Id, text = s.Text, translation = s.Translation })
            .ToListAsync(ct);

        return new
        {
            sentences = rows,
            total_count = totalCount,
            offset,
            has_more = offset + rows.Count < totalCount,
        };
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

        var lemma = word.Lemma.Trim();
        var candidates = await _context.QuizSentences
            .Where(s => s.QuizId == context.QuizId.Value && s.Text.Contains(lemma))
            .ToListAsync(ct);
        var quizSentence = candidates.FirstOrDefault(s => ContainsWord(s.Text, word.Lemma));
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
        var language = FirstNonBlank(GetString(args, "language"), context.CurrentLanguage);
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
        var language = FirstNonBlank(GetString(args, "language"), context.CurrentLanguage);
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

        var word = GetString(args, "word");
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

        var (words, skipped) = GetWordDrafts(args, "words");
        if (words.Count == 0)
        {
            return new { error = "At least one valid word and translation is required.", skipped };
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

        return new { queued = true, kind = "add_words", count = words.Count, skipped };
    }

    private static object QueueAddSentence(JsonElement args, AgentToolContext context)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var text = GetString(args, "text");
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

    private static object QueueAddSentences(JsonElement args, AgentToolContext context)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var (sentences, skipped) = GetSentenceDrafts(args, "sentences");
        if (sentences.Count == 0)
        {
            return new { error = "At least one valid sentence and translation is required.", skipped };
        }

        foreach (var sentence in sentences)
        {
            var payload = JsonSerializer.SerializeToElement(new
            {
                kind = PendingChangeKinds.AddSentence,
                text = sentence.Text,
                translation = sentence.Translation,
            }, JsonOptions);
            context.PendingChanges.Add(new PendingChange(PendingChangeKinds.AddSentence, payload));
        }

        return new { queued = true, kind = "add_sentences", count = sentences.Count, skipped };
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
            word = GetString(args, "word")?.Trim(),
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

        var (changes, skipped) = GetWordEditDrafts(args, "changes");
        if (changes.Count == 0)
        {
            return new { error = "At least one valid word edit is required.", skipped };
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

        return new { queued = true, kind = "edit_words", count = changes.Count, skipped };
    }

    private async Task<WordDraft?> LoadOriginalWordAsync(Guid quizId, string wordId, CancellationToken cancellationToken)
    {
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
        if (wordIds.Count == 0)
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

    private async Task<object> QueueEditSentenceAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var sentenceIdText = GetString(args, "sentence_id");
        var text = GetString(args, "text")?.Trim();
        var translation = GetString(args, "translation")?.Trim();
        if (!Guid.TryParse(sentenceIdText, out var sentenceId))
        {
            return new { error = "sentence_id must be a valid id. Use list_sentences to find sentence ids." };
        }
        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(translation))
        {
            return new { error = "A new text and/or translation is required." };
        }

        var original = await _context.QuizSentences
            .Where(s => s.Id == sentenceId && s.QuizId == context.QuizId.Value)
            .Select(s => new SentenceDraft(s.Text, s.Translation))
            .FirstOrDefaultAsync(cancellationToken);
        if (original == null)
        {
            return new { error = $"Sentence {sentenceId} not found in this quiz. Use list_sentences to find sentence ids." };
        }

        QueueSentenceEdit(context, sentenceId, original, text, translation);
        return new { queued = true, kind = PendingChangeKinds.EditSentence, sentence_id = sentenceId };
    }

    private async Task<object> QueueEditSentencesAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var (changes, parsedSkipped) = GetSentenceEditDrafts(args, "changes");
        var skipped = parsedSkipped.ToList();
        if (changes.Count == 0)
        {
            return new { error = "At least one valid sentence edit is required.", skipped };
        }

        var sentenceIds = changes.Select(change => change.SentenceId).Distinct().ToList();
        var originals = await _context.QuizSentences
            .Where(s => s.QuizId == context.QuizId.Value && sentenceIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Text, s.Translation })
            .ToDictionaryAsync(
                s => s.Id,
                s => new SentenceDraft(s.Text, s.Translation),
                cancellationToken);

        var queued = 0;
        foreach (var change in changes)
        {
            if (!originals.TryGetValue(change.SentenceId, out var original))
            {
                skipped.Add(new SkippedItem(change.Index, "Sentence was not found in this quiz."));
                continue;
            }

            QueueSentenceEdit(context, change.SentenceId, original, change.Text, change.Translation);
            queued++;
        }

        if (queued == 0)
        {
            return new { error = "None of the requested sentences were found in this quiz.", skipped };
        }

        return new { queued = true, kind = "edit_sentences", count = queued, skipped };
    }

    private static void QueueSentenceEdit(
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
            original_text = original.Trim(),
            new_text = newText.Trim(),
            new_translation = newTranslation.Trim(),
        }, JsonOptions);

        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.RepairSentence, payload));
        return new { queued = true, kind = PendingChangeKinds.RepairSentence };
    }

    private async Task<object> QueueDeleteSentenceAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var sentenceIdText = GetString(args, "sentence_id");
        if (!Guid.TryParse(sentenceIdText, out var sentenceId))
        {
            return new { error = "sentence_id must be a valid id. Use list_sentences to find sentence ids." };
        }

        var sentence = await _context.QuizSentences
            .FirstOrDefaultAsync(s => s.Id == sentenceId && s.QuizId == context.QuizId.Value, ct);
        if (sentence == null)
        {
            return new { error = $"Sentence {sentenceId} not found in this quiz. Use list_sentences to find sentence ids." };
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.DeleteSentence,
            sentence_id = sentence.Id,
            text = sentence.Text,
        }, JsonOptions);

        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.DeleteSentence, payload));
        return new { queued = true, kind = PendingChangeKinds.DeleteSentence, sentence_id = sentence.Id };
    }

    private static object QueueCreateQuiz(JsonElement args, AgentToolContext context)
    {
        var name = GetString(args, "name");
        var sourceLanguage = GetString(args, "source_language");
        var targetLanguage = FirstNonBlank(GetString(args, "target_language"), context.CurrentLanguage);
        var collectionId = GetNullableGuidString(args, "collection_id");
        var (words, skippedWords) = GetWordDrafts(args, "words");

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
        return new { queued = true, kind = PendingChangeKinds.CreateQuiz, name = name.Trim(), skipped = skippedWords };
    }

    private static object QueueCreateCollection(JsonElement args, AgentToolContext context)
    {
        var name = GetString(args, "name");
        var language = FirstNonBlank(GetString(args, "language"), context.CurrentLanguage);
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

    private async Task<object> QueueMoveQuizAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        var quizIdText = GetString(args, "quiz_id");
        var destination = GetNullableGuidString(args, "collection_id");
        if (!Guid.TryParse(quizIdText, out var quizId))
        {
            return new { error = "quiz_id must be a valid id. Use list_quizzes to find quiz ids." };
        }
        if (destination.Invalid)
        {
            return new { error = "collection_id must be a valid id." };
        }

        var quiz = await _context.Quizzes
            .FirstOrDefaultAsync(q => q.Id == quizId && q.UserId == context.UserId, cancellationToken);
        if (quiz == null)
        {
            return new { error = $"Quiz {quizId} was not found." };
        }

        Collection? collection = null;
        if (destination.Value.HasValue)
        {
            collection = await _context.Collections.FirstOrDefaultAsync(
                c => c.Id == destination.Value.Value
                    && c.UserId == context.UserId
                    && c.Language == quiz.TargetLanguage,
                cancellationToken);
            if (collection == null)
            {
                return new { error = "The destination collection was not found for this quiz's language." };
            }
        }

        if (quiz.CollectionId == destination.Value)
        {
            return new { error = "The quiz is already in that location." };
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.MoveQuiz,
            quiz_id = quiz.Id,
            quiz_name = quiz.Name,
            collection_id = destination.Value,
            collection_name = collection?.Name,
        }, JsonOptions);
        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.MoveQuiz, payload));
        return new { queued = true, kind = PendingChangeKinds.MoveQuiz, quiz_id = quiz.Id };
    }

    private async Task<object> QueueRenameCollectionAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        var collectionIdText = GetString(args, "collection_id");
        var name = GetString(args, "name")?.Trim();
        if (!Guid.TryParse(collectionIdText, out var collectionId))
        {
            return new { error = "collection_id must be a valid id. Use list_collections to find collection ids." };
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            return new { error = "name is required." };
        }

        var collection = await _context.Collections
            .FirstOrDefaultAsync(c => c.Id == collectionId && c.UserId == context.UserId, cancellationToken);
        if (collection == null)
        {
            return new { error = $"Collection {collectionId} was not found." };
        }
        if (string.Equals(collection.Name, name, StringComparison.Ordinal))
        {
            return new { error = "The collection already has that name." };
        }

        var duplicateExists = await _context.Collections.AnyAsync(c =>
            c.Id != collection.Id
            && c.UserId == context.UserId
            && c.Language == collection.Language
            && c.ParentCollectionId == collection.ParentCollectionId
            && c.Name == name,
            cancellationToken);
        if (duplicateExists)
        {
            return new { error = "A collection with that name already exists in the same location." };
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.RenameCollection,
            collection_id = collection.Id,
            original_name = collection.Name,
            name,
        }, JsonOptions);
        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.RenameCollection, payload));
        return new { queued = true, kind = PendingChangeKinds.RenameCollection, collection_id = collection.Id };
    }

    private async Task<object> QueueMoveCollectionAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        var collectionIdText = GetString(args, "collection_id");
        var destination = GetNullableGuidString(args, "parent_collection_id");
        if (!Guid.TryParse(collectionIdText, out var collectionId))
        {
            return new { error = "collection_id must be a valid id. Use list_collections to find collection ids." };
        }
        if (destination.Invalid)
        {
            return new { error = "parent_collection_id must be a valid id." };
        }
        if (destination.Value == collectionId)
        {
            return new { error = "A collection cannot be moved inside itself." };
        }

        var collection = await _context.Collections
            .FirstOrDefaultAsync(c => c.Id == collectionId && c.UserId == context.UserId, cancellationToken);
        if (collection == null)
        {
            return new { error = $"Collection {collectionId} was not found." };
        }

        Collection? parent = null;
        if (destination.Value.HasValue)
        {
            parent = await _context.Collections.FirstOrDefaultAsync(
                c => c.Id == destination.Value.Value
                    && c.UserId == context.UserId
                    && c.Language == collection.Language,
                cancellationToken);
            if (parent == null)
            {
                return new { error = "The destination collection was not found for this language." };
            }

            var parentMap = await _context.Collections
                .Where(c => c.UserId == context.UserId && c.Language == collection.Language)
                .ToDictionaryAsync(c => c.Id, c => c.ParentCollectionId, cancellationToken);
            var ancestorId = parent.Id;
            var visited = new HashSet<Guid>();
            while (true)
            {
                if (ancestorId == collection.Id)
                {
                    return new { error = "A collection cannot be moved inside one of its descendants." };
                }
                if (!visited.Add(ancestorId))
                {
                    return new { error = "The collection hierarchy contains a cycle and cannot be changed safely." };
                }
                if (!parentMap.TryGetValue(ancestorId, out var nextAncestor) || !nextAncestor.HasValue)
                {
                    break;
                }
                ancestorId = nextAncestor.Value;
            }
        }

        if (collection.ParentCollectionId == destination.Value)
        {
            return new { error = "The collection is already in that location." };
        }

        var duplicateExists = await _context.Collections.AnyAsync(c =>
            c.Id != collection.Id
            && c.UserId == context.UserId
            && c.Language == collection.Language
            && c.ParentCollectionId == destination.Value
            && c.Name == collection.Name,
            cancellationToken);
        if (duplicateExists)
        {
            return new { error = "A collection with that name already exists in the destination." };
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.MoveCollection,
            collection_id = collection.Id,
            collection_name = collection.Name,
            parent_collection_id = destination.Value,
            parent_collection_name = parent?.Name,
        }, JsonOptions);
        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.MoveCollection, payload));
        return new { queued = true, kind = PendingChangeKinds.MoveCollection, collection_id = collection.Id };
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

    private static int GetOffset(JsonElement element)
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

    private static int GetBoundedInt(
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

    private static string? GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    // Models sometimes pass "" for arguments they mean to omit, so blank must fall
    // back the same way as missing.
    private static string? FirstNonBlank(string? value, string? fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
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

    private static object IntegerProp(string description) =>
        new Dictionary<string, object>
        {
            ["type"] = "integer",
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

    private static (IReadOnlyList<WordDraft> Words, IReadOnlyList<SkippedItem> Skipped) GetWordDrafts(
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

    private static (IReadOnlyList<WordEditDraft> Changes, IReadOnlyList<SkippedItem> Skipped) GetWordEditDrafts(
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

    private static (IReadOnlyList<SentenceDraft> Sentences, IReadOnlyList<SkippedItem> Skipped) GetSentenceDrafts(
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

    private static (IReadOnlyList<SentenceEditDraft> Changes, IReadOnlyList<SkippedItem> Skipped) GetSentenceEditDrafts(
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

    private static object SentenceArrayProp(string description) =>
        new Dictionary<string, object>
        {
            ["type"] = "array",
            ["description"] = description,
            ["items"] = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["text"] = StringProp("Natural full sentence in the target language."),
                    ["translation"] = StringProp("Natural translation in the source language."),
                },
                ["required"] = new[] { "text", "translation" },
            },
        };

    private readonly record struct NullableGuidString(Guid? Value, bool Invalid);
    private sealed record WordDraft(string Word, string Translation);
    private readonly record struct WordEditDraft(string WordId, string? Word, string? Translation);
    private sealed record SentenceDraft(string Text, string Translation);
    private readonly record struct SentenceEditDraft(
        int Index,
        Guid SentenceId,
        string? Text,
        string? Translation);
    private sealed record SkippedItem(int Index, string Reason);

}
