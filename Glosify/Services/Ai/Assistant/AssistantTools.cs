using System.Text.Json;
using Glosify.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using Glosify.Services.Ai.Llm;
using Glosify.Services.CustomQuizzes;

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
        "create_vocabulary_quiz",
        "Propose creating a standard vocabulary quiz with words and translations. This tool does not create an interactive custom-quiz document. The change is only saved when the user clicks Apply.",
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

    private static readonly AgentToolDeclaration ListCustomQuizzesDeclaration = new(
        "list_custom_quizzes",
        "List custom quizzes owned by the user, optionally limited to a backing quiz. Use this to find an existing custom quiz before inspecting or changing its elements.",
        BuildSchema(new Dictionary<string, object>
        {
            ["quiz_id"] = StringProp("Optional backing quiz id. Defaults to the current quiz when one is selected."),
        }));

    private static readonly AgentToolDeclaration ListCustomQuizTemplatesDeclaration = new(
        "list_custom_quiz_templates",
        "List curated visual and layout templates for custom quizzes. Use this before creating or substantially redesigning a custom quiz, then follow the selected template's layout guidance while adding individual elements.",
        BuildSchema([]));

    private static readonly AgentToolDeclaration GetCustomQuizDeclaration = new(
        "get_custom_quiz",
        "Get a custom quiz's complete element document and validation state. In the custom quiz creator, omit custom_quiz_id to inspect the open custom quiz. Always inspect it before configuring or removing elements.",
        BuildSchema(new Dictionary<string, object>
        {
            ["custom_quiz_id"] = StringProp("Optional custom quiz id. Defaults to the custom quiz open in the creator."),
        }));

    private static readonly AgentToolDeclaration CreateCustomQuizDeclaration = new(
        "create_custom_quiz",
        "Start a new empty custom quiz for an existing backing quiz. This queues only the quiz shell. After it succeeds, add every element with a separate add_label, add_text_input, add_checkbox, add_choice, add_word_bank, add_submit_button, add_feedback_message, or add_custom_quiz_element call. Never pass a complete document here. Queued until Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["quiz_id"] = StringProp("Optional backing quiz id. Defaults to the selected quiz."),
            ["name"] = StringProp("Custom quiz name."),
            ["template_id"] = StringProp("Optional id from list_custom_quiz_templates. Sets the visual style; follow that template's layout guidance when adding elements."),
        }, required: ["name"]));

    private static readonly AgentToolDeclaration CreateCustomQuizFromContentDeclaration = new(
        "create_custom_quiz_from_content",
        "Start a new backing vocabulary quiz and an empty custom quiz from source material such as the current book page. This queues only the quiz shells and starter words. After it succeeds, add every custom element with a separate element tool call; word bindings in those calls may use the exact word values supplied here. Queued until Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["quiz_name"] = StringProp("Name of the backing vocabulary quiz."),
            ["custom_quiz_name"] = StringProp("Name shown for the custom quiz."),
            ["source_language"] = StringProp("Language the user already knows."),
            ["target_language"] = StringProp("Language being learned. Defaults to the current app language."),
            ["collection_id"] = StringProp("Optional collection id for the backing quiz."),
            ["template_id"] = StringProp("Optional id from list_custom_quiz_templates. Sets the visual style for the custom quiz."),
            ["words"] = WordArrayProp("Starter vocabulary needed by the custom quiz."),
        }, required: ["quiz_name", "custom_quiz_name", "source_language", "words"]));

    private static readonly AgentToolDeclaration AddLabelDeclaration = AtomicElementDeclaration(
        "add_label",
        "Add one visible label to the custom quiz. Call once per label. label_type may be instruction_label, prompt_label, translation_label, or quiz_heading.",
        new Dictionary<string, object>
        {
            ["id"] = StringProp("Stable unique element id."),
            ["text"] = StringProp("Visible label text."),
            ["label_type"] = EnumProp("Label type.", "instruction_label", "prompt_label", "translation_label", "quiz_heading"),
        }, ["id", "text"]);

    private static readonly AgentToolDeclaration AddTextInputDeclaration = AtomicElementDeclaration(
        "add_text_input",
        "Add one graded text answer to the custom quiz. Call once per question. Single-line inputs render as compact inline blanks: put {{blank}} in the learner-visible label exactly where the answer belongs. Never draw a blank with underscores or dots. Use expected_text for literal endings; otherwise use expected_binding.",
        new Dictionary<string, object>
        {
            ["id"] = StringProp("Stable unique element id."),
            ["label"] = StringProp("The complete, specific exercise row with {{blank}} at the answer position, for example '1. ja jest{{blank}}'. Do not include underscores or a second visual blank."),
            ["answer_type"] = EnumProp("Use text_input for one line or textarea for a long answer.", "text_input", "textarea"),
            ["expected_text"] = StringProp("Literal correct answer, such as a verb ending."),
            ["expected_binding"] = FlexibleBindingProp(),
        }, ["id", "label"]);

    private static readonly AgentToolDeclaration AddCheckboxDeclaration = AtomicElementDeclaration(
        "add_checkbox",
        "Add one graded checkbox to the custom quiz. Call once per checkbox question.",
        new Dictionary<string, object>
        {
            ["id"] = StringProp("Stable unique element id."),
            ["label"] = StringProp("Specific learner-visible checkbox question."),
            ["expected_checked"] = new Dictionary<string, object> { ["type"] = "boolean" },
        }, ["id", "label", "expected_checked"]);

    private static readonly AgentToolDeclaration AddChoiceDeclaration = AtomicElementDeclaration(
        "add_choice",
        "Add one graded choice question. Call once per question. Supply at least two options and mark the correct selection or selections.",
        new Dictionary<string, object>
        {
            ["id"] = StringProp("Stable unique element id."),
            ["label"] = StringProp("Specific learner-visible choice question."),
            ["choice_type"] = EnumProp("Choice control type.", "radio_group", "multi_select_group", "select_menu"),
            ["options"] = FlexibleOptionsProp(),
        }, ["id", "label", "choice_type", "options"]);

    private static readonly AgentToolDeclaration AddWordBankDeclaration = AtomicElementDeclaration(
        "add_word_bank",
        "Add one word bank targeting existing text inputs or textareas.",
        new Dictionary<string, object>
        {
            ["id"] = StringProp("Stable unique element id."),
            ["target_input_ids"] = StringArrayProp("Ids of text inputs or textareas filled by this word bank."),
        }, ["id", "target_input_ids"]);

    private static readonly AgentToolDeclaration AddSubmitButtonDeclaration = AtomicElementDeclaration(
        "add_submit_button",
        "Add the custom quiz's single submit button. Call exactly once per new quiz.",
        new Dictionary<string, object>
        {
            ["id"] = StringProp("Stable unique element id."),
            ["text"] = StringProp("Button text, such as Check answers."),
        }, ["id"]);

    private static readonly AgentToolDeclaration AddFeedbackMessageDeclaration = AtomicElementDeclaration(
        "add_feedback_message",
        "Add the custom quiz's single feedback message element. Call exactly once per new quiz.",
        new Dictionary<string, object> { ["id"] = StringProp("Stable unique element id.") }, ["id"]);

    private static readonly AgentToolDeclaration AddCustomQuizElementDeclaration = new(
        "add_custom_quiz_element",
        "Add exactly one custom quiz element not covered by a more specific add tool. Never pass an array or a complete quiz document. Queued until Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["custom_quiz_id"] = StringProp("Optional existing custom quiz id. Omit for the open quiz or the new quiz shell started earlier in this turn."),
            ["element"] = CustomQuizBlockProp(useWordReference: false, requireType: true),
        }, required: ["element"]));

    private static readonly AgentToolDeclaration AddCustomQuizElementsDeclaration = new(
        "add_custom_quiz_elements",
        "Propose adding one or more configured elements to an existing custom quiz. Inspect the custom quiz and list its words first. Queued until Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["custom_quiz_id"] = StringProp("Optional custom quiz id. Defaults to the custom quiz open in the creator."),
            ["blocks"] = CustomQuizBlocksProp(useWordReference: false),
        }, required: ["blocks"]));

    private static readonly AgentToolDeclaration ConfigureCustomQuizElementDeclaration = new(
        "configure_custom_quiz_element",
        "Propose changing an existing custom quiz element. Only supplied settings are changed. options replaces the full option list. Queued until Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["custom_quiz_id"] = StringProp("Optional custom quiz id. Defaults to the custom quiz open in the creator."),
            ["block_id"] = StringProp("Existing element id from get_custom_quiz."),
            ["settings"] = CustomQuizBlockProp(useWordReference: false, requireType: false),
        }, required: ["block_id", "settings"]));

    private static readonly AgentToolDeclaration RemoveCustomQuizElementDeclaration = new(
        "remove_custom_quiz_element",
        "Propose removing an element from an existing custom quiz. Inspect the quiz first and use its exact element id. Queued until Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["custom_quiz_id"] = StringProp("Optional custom quiz id. Defaults to the custom quiz open in the creator."),
            ["block_id"] = StringProp("Element id to remove."),
        }, required: ["block_id"]));

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
        ListCustomQuizzesDeclaration,
        ListCustomQuizTemplatesDeclaration,
        GetCustomQuizDeclaration,
        CreateCustomQuizDeclaration,
        CreateCustomQuizFromContentDeclaration,
        AddLabelDeclaration,
        AddTextInputDeclaration,
        AddCheckboxDeclaration,
        AddChoiceDeclaration,
        AddWordBankDeclaration,
        AddSubmitButtonDeclaration,
        AddFeedbackMessageDeclaration,
        AddCustomQuizElementDeclaration,
        ConfigureCustomQuizElementDeclaration,
        RemoveCustomQuizElementDeclaration,
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
            "create_vocabulary_quiz" => QueueCreateQuiz(args, context),
            "create_collection" => QueueCreateCollection(args, context),
            "move_quiz" => await QueueMoveQuizAsync(args, context, cancellationToken),
            "rename_collection" => await QueueRenameCollectionAsync(args, context, cancellationToken),
            "move_collection" => await QueueMoveCollectionAsync(args, context, cancellationToken),
            "list_custom_quizzes" => await ListCustomQuizzesAsync(args, context, cancellationToken),
            "list_custom_quiz_templates" => ListCustomQuizTemplates(),
            "get_custom_quiz" => await GetCustomQuizAsync(args, context, cancellationToken),
            "create_custom_quiz" => await QueueCreateCustomQuizAsync(args, context, cancellationToken),
            "create_custom_quiz_from_content" => QueueCreateCustomQuizFromContent(args, context),
            "add_label" or "add_text_input" or "add_checkbox" or "add_choice" or "add_word_bank" or "add_submit_button" or "add_feedback_message" or "add_custom_quiz_element"
                => await QueueAtomicCustomQuizElementAsync(name, args, context, cancellationToken),
            "add_custom_quiz_elements" => await QueueAddCustomQuizElementsAsync(args, context, cancellationToken),
            "configure_custom_quiz_element" => await QueueConfigureCustomQuizElementAsync(args, context, cancellationToken),
            "remove_custom_quiz_element" => await QueueRemoveCustomQuizElementAsync(args, context, cancellationToken),
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
        JsonElement? customQuiz = null;
        if (args.TryGetProperty("custom_quiz", out var customQuizElement)
            && customQuizElement.ValueKind != JsonValueKind.Null)
        {
            if (customQuizElement.ValueKind != JsonValueKind.Object
                || string.IsNullOrWhiteSpace(GetString(customQuizElement, "name"))
                || !TryGetArray(customQuizElement, "blocks", out var customBlocks)
                || customBlocks.GetArrayLength() == 0)
            {
                return new { error = "custom_quiz requires a name and at least one block." };
            }
            if (words.Count == 0)
            {
                return new { error = "A custom quiz created with a new quiz needs starter words for its bindings." };
            }
            var promptError = ValidateAssistantAnswerPrompts(customBlocks);
            if (promptError != null)
            {
                return InvalidCustomQuizPrompts(promptError);
            }
            customQuiz = customQuizElement.Clone();
        }

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
            custom_quiz = customQuiz,
        }, JsonOptions);

        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.CreateQuiz, payload));
        return new
        {
            queued = true,
            kind = PendingChangeKinds.CreateQuiz,
            name = name.Trim(),
            includes_custom_quiz = customQuiz.HasValue,
            skipped = skippedWords,
        };
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

    private async Task<object> ListCustomQuizzesAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        Guid? quizId = context.QuizId;
        var suppliedQuizId = GetString(args, "quiz_id");
        if (!string.IsNullOrWhiteSpace(suppliedQuizId))
        {
            if (!Guid.TryParse(suppliedQuizId, out var parsed))
            {
                return new { error = "quiz_id must be a valid id." };
            }
            quizId = parsed;
        }

        var query = _context.CustomQuizzes
            .AsNoTracking()
            .Where(item => item.Quiz.UserId == context.UserId);
        if (quizId.HasValue)
        {
            query = query.Where(item => item.QuizId == quizId.Value);
        }

        var rows = await query
            .OrderBy(item => item.Quiz.Name)
            .ThenBy(item => item.Name)
            .Select(item => new
            {
                id = item.Id,
                name = item.Name,
                quiz_id = item.QuizId,
                quiz_name = item.Quiz.Name,
                is_playable = item.IsPlayable,
                updated_at = item.UpdatedAt,
            })
            .ToListAsync(ct);
        return new { custom_quizzes = rows, count = rows.Count };
    }

    private static object ListCustomQuizTemplates()
    {
        var templates = new CustomQuizTemplateCatalog().List().Select(template => new
        {
            id = template.Id,
            name = template.Name,
            description = template.Description,
            style_preset = template.StylePreset,
            best_for = template.BestFor,
            layout_guidance = template.LayoutGuidance,
        }).ToList();
        return new { templates, count = templates.Count };
    }

    private async Task<object> GetCustomQuizAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var resolved = ResolveCustomQuizId(args, context);
        if (resolved.Error != null)
        {
            return new { error = resolved.Error };
        }

        var item = await new Glosify.Services.CustomQuizzes.CustomQuizService(_context)
            .GetForEditorAsync(resolved.Id!.Value, context.UserId, ct);
        if (item == null)
        {
            return new { error = "That custom quiz was not found." };
        }

        return new
        {
            id = item.Id,
            quiz_id = item.QuizId,
            name = item.Name,
            is_playable = item.IsPlayable,
            validation_errors = item.PlayabilityErrors,
            document = item.Document,
        };
    }

    private async Task<object> QueueCreateCustomQuizAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var quizId = context.QuizId;
        var suppliedQuizId = GetString(args, "quiz_id");
        if (!string.IsNullOrWhiteSpace(suppliedQuizId))
        {
            if (!Guid.TryParse(suppliedQuizId, out var parsed))
            {
                return new { error = "quiz_id must be a valid id." };
            }
            quizId = parsed;
        }

        var name = GetString(args, "name")?.Trim();
        var template = ResolveCustomQuizTemplate(args);
        if (!quizId.HasValue || string.IsNullOrWhiteSpace(name))
        {
            return new { error = "Choose a backing quiz and provide a custom quiz name." };
        }
        if (args.TryGetProperty("blocks", out _))
        {
            return new { error = "create_custom_quiz queues only the empty quiz shell. Call one element tool per element after creating the shell." };
        }
        if (!await _context.Quizzes.AnyAsync(quiz => quiz.Id == quizId.Value && quiz.UserId == context.UserId, ct))
        {
            return QuizContextRequired();
        }

        var draftRef = $"custom-{Guid.NewGuid():N}";
        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.CreateCustomQuiz,
            quiz_id = quizId.Value,
            name,
            draft_ref = draftRef,
            style_preset = template?.StylePreset ?? CustomQuizStylePresets.Editorial,
        }, JsonOptions);
        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.CreateCustomQuiz, payload));
        context.PendingCustomQuizRef = draftRef;
        context.PendingCustomQuizName = name;
        return new { queued = true, kind = PendingChangeKinds.CreateCustomQuiz, name, draft_ref = draftRef, next = "Add each element with a separate element tool call." };
    }

    private static object QueueCreateCustomQuizFromContent(JsonElement args, AgentToolContext context)
    {
        var quizName = GetString(args, "quiz_name")?.Trim();
        var customQuizName = GetString(args, "custom_quiz_name")?.Trim();
        var sourceLanguage = GetString(args, "source_language")?.Trim();
        var targetLanguage = FirstNonBlank(GetString(args, "target_language"), context.CurrentLanguage)?.Trim();
        var collectionId = GetNullableGuidString(args, "collection_id");
        var template = ResolveCustomQuizTemplate(args);
        var (words, skippedWords) = GetWordDrafts(args, "words");
        if (string.IsNullOrWhiteSpace(quizName)
            || string.IsNullOrWhiteSpace(customQuizName)
            || string.IsNullOrWhiteSpace(sourceLanguage))
        {
            return new { error = "quiz_name, custom_quiz_name, and source_language are required." };
        }
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            return new { error = "target_language is required when no current app language is selected." };
        }
        if (collectionId.Invalid)
        {
            return new { error = "collection_id must be a valid id." };
        }
        if (words.Count == 0)
        {
            return new { error = "At least one starter word is required." };
        }
        if (args.TryGetProperty("blocks", out _))
        {
            return new { error = "create_custom_quiz_from_content queues only the quiz shells and starter words. Call one element tool per element afterward." };
        }

        var draftRef = $"custom-{Guid.NewGuid():N}";
        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.CreateQuiz,
            name = quizName,
            source_language = sourceLanguage,
            target_language = targetLanguage,
            collection_id = collectionId.Value,
            words,
            custom_quiz = new
            {
                name = customQuizName,
                draft_ref = draftRef,
                style_preset = template?.StylePreset ?? CustomQuizStylePresets.Editorial,
            },
        }, JsonOptions);
        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.CreateQuiz, payload));
        context.PendingCustomQuizRef = draftRef;
        context.PendingCustomQuizName = customQuizName;
        return new
        {
            queued = true,
            kind = PendingChangeKinds.CreateQuiz,
            includes_custom_quiz = true,
            name = quizName,
            custom_quiz_name = customQuizName,
            draft_ref = draftRef,
            next = "Add each element with a separate element tool call.",
            skipped = skippedWords,
        };
    }

    private static CustomQuizTemplateSummary? ResolveCustomQuizTemplate(JsonElement args)
    {
        var templateId = GetString(args, "template_id");
        return string.IsNullOrWhiteSpace(templateId)
            ? null
            : new CustomQuizTemplateCatalog().List().FirstOrDefault(template => template.Id == templateId);
    }

    private async Task<object> QueueAtomicCustomQuizElementAsync(
        string toolName,
        JsonElement args,
        AgentToolContext context,
        CancellationToken ct)
    {
        var target = await ResolveCustomQuizTargetAsync(args, context, ct);
        if (target.Error != null)
        {
            return new { error = target.Error };
        }

        JsonElement block;
        if (toolName == "add_custom_quiz_element")
        {
            if (!args.TryGetProperty("element", out var supplied) || supplied.ValueKind != JsonValueKind.Object)
            {
                return new { error = "element is required and must be one custom quiz element." };
            }
            block = supplied.Clone();
        }
        else
        {
            var type = toolName switch
            {
                "add_label" => FirstNonBlank(GetString(args, "label_type"), CustomQuizBlockTypes.InstructionLabel),
                "add_text_input" => FirstNonBlank(GetString(args, "answer_type"), CustomQuizBlockTypes.TextInput),
                "add_checkbox" => CustomQuizBlockTypes.Checkbox,
                "add_choice" => GetString(args, "choice_type"),
                "add_word_bank" => CustomQuizBlockTypes.WordBank,
                "add_submit_button" => CustomQuizBlockTypes.SubmitButton,
                "add_feedback_message" => CustomQuizBlockTypes.FeedbackMessage,
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(type) || !CustomQuizBlockTypes.All.Contains(type))
            {
                return new { error = "A valid element type is required." };
            }

            var properties = new Dictionary<string, object?> { ["type"] = type };
            foreach (var property in args.EnumerateObject())
            {
                if (property.Name is "custom_quiz_id" or "label_type" or "answer_type" or "choice_type") continue;
                properties[property.Name] = property.Value.Clone();
            }
            block = JsonSerializer.SerializeToElement(properties, JsonOptions);
        }

        var blockId = GetString(block, "id")?.Trim();
        var blockType = GetString(block, "type")?.Trim();
        if (string.IsNullOrWhiteSpace(blockId) || string.IsNullOrWhiteSpace(blockType))
        {
            return new { error = "Every element needs a stable id and type." };
        }
        var promptError = ValidateAssistantAnswerPrompts(JsonSerializer.SerializeToElement(new[] { block }, JsonOptions), requireAnswer: false);
        if (promptError != null)
        {
            return InvalidCustomQuizPrompts(promptError);
        }
        var label = GetString(block, "label")?.Trim();
        if (CustomQuizBlockTypes.IsAnswer(blockType)
            && !string.IsNullOrWhiteSpace(label)
            && AnswerLabelAlreadyExists(context, target, label))
        {
            return InvalidCustomQuizPrompts($"The answer question label \"{label}\" is already used in this custom quiz.");
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.AddCustomQuizElement,
            custom_quiz_id = target.Id,
            custom_quiz_ref = target.DraftRef,
            custom_quiz_name = target.Name,
            block,
            binding_words_from_draft = target.DraftRef != null,
        }, JsonOptions);
        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.AddCustomQuizElement, payload));
        return new { queued = true, kind = toolName, element_id = blockId, element_type = blockType };
    }

    private async Task<object> QueueAddCustomQuizElementsAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var resolved = ResolveCustomQuizId(args, context);
        if (resolved.Error != null)
        {
            return new { error = resolved.Error };
        }
        if (!TryGetArray(args, "blocks", out var blocks) || blocks.GetArrayLength() == 0)
        {
            return new { error = "blocks must contain at least one custom quiz element." };
        }
        var promptError = ValidateAssistantAnswerPrompts(blocks, requireAnswer: false);
        if (promptError != null)
        {
            return InvalidCustomQuizPrompts(promptError);
        }
        var item = await LoadOwnedCustomQuizAsync(resolved.Id!.Value, context.UserId, ct);
        if (item == null)
        {
            return new { error = "That custom quiz was not found." };
        }
        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.AddCustomQuizElements,
            custom_quiz_id = item.Id,
            custom_quiz_name = item.Name,
            blocks = blocks.Clone(),
        }, JsonOptions);
        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.AddCustomQuizElements, payload));
        return new { queued = true, kind = PendingChangeKinds.AddCustomQuizElements, element_count = blocks.GetArrayLength() };
    }

    private async Task<object> QueueConfigureCustomQuizElementAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var resolved = ResolveCustomQuizId(args, context);
        if (resolved.Error != null)
        {
            return new { error = resolved.Error };
        }
        var blockId = GetString(args, "block_id")?.Trim();
        if (string.IsNullOrWhiteSpace(blockId)
            || !args.TryGetProperty("settings", out var settings)
            || settings.ValueKind != JsonValueKind.Object)
        {
            return new { error = "block_id and settings are required." };
        }
        var item = await LoadOwnedCustomQuizAsync(resolved.Id!.Value, context.UserId, ct);
        if (item == null)
        {
            return new { error = "That custom quiz was not found." };
        }
        if (settings.TryGetProperty("label", out var configuredLabel)
            && (configuredLabel.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(configuredLabel.GetString())))
        {
            return InvalidCustomQuizPrompts($"Element {blockId} cannot have an empty question label.");
        }
        if (!CustomQuizDocumentContainsBlock(item.DefinitionJson, blockId))
        {
            return new { error = $"Element {blockId} was not found in that custom quiz. Inspect it again before configuring elements." };
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.ConfigureCustomQuizElement,
            custom_quiz_id = item.Id,
            custom_quiz_name = item.Name,
            block_id = blockId,
            settings = settings.Clone(),
        }, JsonOptions);
        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.ConfigureCustomQuizElement, payload));
        return new { queued = true, kind = PendingChangeKinds.ConfigureCustomQuizElement, block_id = blockId };
    }

    private async Task<object> QueueRemoveCustomQuizElementAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var resolved = ResolveCustomQuizId(args, context);
        if (resolved.Error != null)
        {
            return new { error = resolved.Error };
        }
        var blockId = GetString(args, "block_id")?.Trim();
        if (string.IsNullOrWhiteSpace(blockId))
        {
            return new { error = "block_id is required." };
        }
        var item = await LoadOwnedCustomQuizAsync(resolved.Id!.Value, context.UserId, ct);
        if (item == null)
        {
            return new { error = "That custom quiz was not found." };
        }
        if (!CustomQuizDocumentContainsBlock(item.DefinitionJson, blockId))
        {
            return new { error = $"Element {blockId} was not found in that custom quiz." };
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.RemoveCustomQuizElement,
            custom_quiz_id = item.Id,
            custom_quiz_name = item.Name,
            block_id = blockId,
        }, JsonOptions);
        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.RemoveCustomQuizElement, payload));
        return new { queued = true, kind = PendingChangeKinds.RemoveCustomQuizElement, block_id = blockId };
    }

    private async Task<CustomQuiz?> LoadOwnedCustomQuizAsync(Guid id, string userId, CancellationToken ct) =>
        await _context.CustomQuizzes
            .AsNoTracking()
            .Include(item => item.Quiz)
            .FirstOrDefaultAsync(item => item.Id == id && item.Quiz.UserId == userId, ct);

    private async Task<CustomQuizTarget> ResolveCustomQuizTargetAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken ct)
    {
        var supplied = GetString(args, "custom_quiz_id");
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            if (!Guid.TryParse(supplied, out var parsed))
            {
                return new(null, null, string.Empty, null, "custom_quiz_id must be a valid id.");
            }
            var item = await LoadOwnedCustomQuizAsync(parsed, context.UserId, ct);
            return item == null
                ? new(null, null, string.Empty, null, "That custom quiz was not found.")
                : new(item.Id, null, item.Name, item.DefinitionJson, null);
        }

        if (!string.IsNullOrWhiteSpace(context.PendingCustomQuizRef))
        {
            return new(null, context.PendingCustomQuizRef, context.PendingCustomQuizName ?? "New custom quiz", null, null);
        }

        if (context.CustomQuizId.HasValue)
        {
            var item = await LoadOwnedCustomQuizAsync(context.CustomQuizId.Value, context.UserId, ct);
            return item == null
                ? new(null, null, string.Empty, null, "The open custom quiz was not found.")
                : new(item.Id, null, item.Name, item.DefinitionJson, null);
        }

        return new(null, null, string.Empty, null, "Start or open a custom quiz before adding elements.");
    }

    private static bool AnswerLabelAlreadyExists(AgentToolContext context, CustomQuizTarget target, string label)
    {
        if (!string.IsNullOrWhiteSpace(target.DefinitionJson))
        {
            try
            {
                using var document = JsonDocument.Parse(target.DefinitionJson);
                if (document.RootElement.TryGetProperty("blocks", out var existing)
                    && existing.ValueKind == JsonValueKind.Array
                    && existing.EnumerateArray().Any(block =>
                        CustomQuizBlockTypes.IsAnswer(GetString(block, "type") ?? string.Empty)
                        && string.Equals(GetString(block, "label")?.Trim(), label, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // Stored-document validation is handled by the custom quiz service.
            }
        }

        return context.PendingChanges
            .Where(change => change.Kind == PendingChangeKinds.AddCustomQuizElement)
            .Where(change => TargetMatches(change.Payload, target))
            .Select(change => change.Payload.TryGetProperty("block", out var block) ? block : default)
            .Any(block => block.ValueKind == JsonValueKind.Object
                && CustomQuizBlockTypes.IsAnswer(GetString(block, "type") ?? string.Empty)
                && string.Equals(GetString(block, "label")?.Trim(), label, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TargetMatches(JsonElement payload, CustomQuizTarget target)
    {
        if (target.Id.HasValue)
        {
            return Guid.TryParse(GetString(payload, "custom_quiz_id"), out var id) && id == target.Id.Value;
        }
        return string.Equals(GetString(payload, "custom_quiz_ref"), target.DraftRef, StringComparison.Ordinal);
    }

    private static (Guid? Id, string? Error) ResolveCustomQuizId(JsonElement args, AgentToolContext context)
    {
        var supplied = GetString(args, "custom_quiz_id");
        if (string.IsNullOrWhiteSpace(supplied))
        {
            return context.CustomQuizId.HasValue
                ? (context.CustomQuizId, null)
                : (null, "Choose or open a custom quiz first.");
        }
        return Guid.TryParse(supplied, out var parsed)
            ? (parsed, null)
            : (null, "custom_quiz_id must be a valid id.");
    }

    private static bool CustomQuizDocumentContainsBlock(string definitionJson, string blockId)
    {
        try
        {
            using var document = JsonDocument.Parse(definitionJson);
            return document.RootElement.TryGetProperty("blocks", out var blocks)
                && blocks.ValueKind == JsonValueKind.Array
                && blocks.EnumerateArray().Any(block => string.Equals(GetString(block, "id"), blockId, StringComparison.Ordinal));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static object InvalidCustomQuizPrompts(string error) => new
    {
        error,
        invalid_custom_quiz_questions = true,
        correction = "Give every answer control a specific learner-visible label containing that question's prompt or gap. For text inputs, put {{blank}} where the compact real input belongs and never draw a second blank with underscores or dots. Use different labels for different answers, then call the custom quiz tool again.",
    };

    private static string? ValidateAssistantAnswerPrompts(JsonElement blocks, bool requireAnswer = true)
    {
        var answers = blocks.EnumerateArray()
            .Where(block => block.ValueKind == JsonValueKind.Object
                && CustomQuizBlockTypes.IsAnswer(GetString(block, "type") ?? string.Empty))
            .Select(block => new
            {
                Id = FirstNonBlank(GetString(block, "id"), "unnamed"),
                Type = GetString(block, "type") ?? string.Empty,
                Label = FirstNonBlank(GetString(block, "label"), GetString(block, "text"))?.Trim(),
            })
            .ToList();

        if (requireAnswer && answers.Count == 0)
        {
            return "A custom quiz must contain at least one answer control with its own visible question label.";
        }

        var missing = answers.Where(answer => string.IsNullOrWhiteSpace(answer.Label)).Select(answer => answer.Id).ToList();
        if (missing.Count > 0)
        {
            return $"Answer elements are missing learner-visible question labels: {string.Join(", ", missing)}.";
        }

        var drawnBlanks = answers
            .Where(answer => answer.Type == CustomQuizBlockTypes.TextInput
                && System.Text.RegularExpressions.Regex.IsMatch(answer.Label!, @"_{2,}|\.{3,}"))
            .Select(answer => answer.Id)
            .ToList();
        if (drawnBlanks.Count > 0)
        {
            return $"Text input labels must use {{{{blank}}}} for the real inline control instead of underscores or dots: {string.Join(", ", drawnBlanks)}.";
        }

        var duplicates = answers
            .GroupBy(answer => answer.Label!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => $"\"{group.Key}\"")
            .ToList();
        return duplicates.Count == 0
            ? null
            : $"Answer question labels must be distinct. Repeated: {string.Join(", ", duplicates)}.";
    }

    private static bool TryGetArray(JsonElement element, string property, out JsonElement array)
    {
        if (element.TryGetProperty(property, out array) && array.ValueKind == JsonValueKind.Array)
        {
            return true;
        }
        array = default;
        return false;
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

    private static AgentToolDeclaration AtomicElementDeclaration(
        string name,
        string description,
        Dictionary<string, object> properties,
        IReadOnlyList<string> required)
    {
        properties["custom_quiz_id"] = StringProp("Optional existing custom quiz id. Omit for the open quiz or the new quiz shell started earlier in this turn.");
        properties["column_span"] = EnumProp("Element width in the 12-column layout.", 3, 4, 6, 12);
        properties["grid_column"] = IntegerProp("Optional start column from 1 to 12.");
        properties["grid_row"] = IntegerProp("Optional row from 1 to 500.");
        return new AgentToolDeclaration(name, description + " The change is queued separately until Apply.", BuildSchema(properties, required));
    }

    private static object EnumProp(string description, params object[] values) =>
        new Dictionary<string, object>
        {
            ["type"] = values.Length > 0 && values[0] is int ? "integer" : "string",
            ["enum"] = values,
            ["description"] = description,
        };

    private static object StringArrayProp(string description) =>
        new Dictionary<string, object>
        {
            ["type"] = "array",
            ["description"] = description,
            ["items"] = new Dictionary<string, object> { ["type"] = "string" },
        };

    private static object FlexibleBindingProp() =>
        new Dictionary<string, object>
        {
            ["type"] = "object",
            ["description"] = "Expected word binding. Use word_id from list_words for an existing backing quiz, or exact word for a backing quiz just started from content.",
            ["properties"] = new Dictionary<string, object>
            {
                ["word_id"] = StringProp("Existing backing-quiz word id."),
                ["word"] = StringProp("Exact starter word when the backing quiz is pending creation."),
                ["field"] = EnumProp("Word side to expect.", "lemma", "translation"),
            },
            ["required"] = new[] { "field" },
        };

    private static object FlexibleOptionsProp() =>
        new Dictionary<string, object>
        {
            ["type"] = "array",
            ["items"] = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["id"] = StringProp("Stable unique option id."),
                    ["binding"] = FlexibleBindingProp(),
                    ["is_correct"] = new Dictionary<string, object> { ["type"] = "boolean" },
                },
                ["required"] = new[] { "id", "binding", "is_correct" },
            },
        };

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

    private static object CustomQuizBlocksProp(bool useWordReference) =>
        new Dictionary<string, object>
        {
            ["type"] = "array",
            ["description"] = "Custom quiz elements in display order. A playable quiz needs exactly one submit_button, exactly one feedback_message, and at least one answer element. Every answer element must have a non-empty, learner-visible label; when there are multiple answers, their labels must be distinct questions.",
            ["items"] = CustomQuizBlockProp(useWordReference, requireType: true),
        };

    private static object CustomQuizBlockProp(bool useWordReference, bool requireType)
    {
        var bindingKey = useWordReference ? "word" : "word_id";
        var binding = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["description"] = useWordReference
                ? "Bind to the exact target-language word supplied in the starter words array."
                : "Bind to a word in the backing quiz.",
            ["properties"] = new Dictionary<string, object>
            {
                [bindingKey] = StringProp(useWordReference ? "Exact word value from starter words." : "Word id returned by list_words."),
                ["field"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["enum"] = new[] { "lemma", "translation" },
                    ["description"] = "Which side of the word to display or expect.",
                },
            },
            ["required"] = new[] { bindingKey, "field" },
        };
        var option = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["id"] = StringProp("Stable unique option id."),
                ["binding"] = binding,
                ["is_correct"] = new Dictionary<string, object> { ["type"] = "boolean" },
            },
            ["required"] = new[] { "id", "binding", "is_correct" },
        };
        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["id"] = StringProp("Stable unique element id. Use short descriptive ids so word banks can target inputs."),
                ["type"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["enum"] = CustomQuizBlockTypes.All.Order().ToArray(),
                    ["description"] = "Element type: labels display text or bound words; answer controls are graded; word_bank targets text inputs.",
                },
                ["column_span"] = new Dictionary<string, object> { ["type"] = "integer", ["enum"] = new[] { 3, 4, 6, 12 } },
                ["grid_column"] = IntegerProp("Optional start column from 1 to 12. Layout is normalized to avoid overlaps."),
                ["grid_row"] = IntegerProp("Optional row from 1 to 500. Layout is normalized to avoid overlaps."),
                ["text"] = StringProp("Text for headings, instructions, and submit buttons."),
                ["label"] = StringProp("Required learner-visible question or prompt for every answer control. For text_input, put {{blank}} exactly where the compact inline answer belongs (for example, '1. ja jest{{blank}}'); never use underscores or dots to draw a blank. Keep labels distinct."),
                ["binding"] = binding,
                ["expected_binding"] = binding,
                ["expected_text"] = StringProp("Literal correct answer for text inputs, such as a verb ending. Use instead of expected_binding when the learner should enter only part of a word."),
                ["expected_checked"] = new Dictionary<string, object> { ["type"] = "boolean" },
                ["options"] = new Dictionary<string, object> { ["type"] = "array", ["items"] = option },
                ["target_input_ids"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object> { ["type"] = "string" },
                    ["description"] = "For word_bank only: ids of text_input or textarea elements it fills.",
                },
            },
        };
        if (requireType)
        {
            schema["required"] = new[] { "type" };
        }
        return schema;
    }

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
    private sealed record CustomQuizTarget(Guid? Id, string? DraftRef, string Name, string? DefinitionJson, string? Error);
    private sealed record SkippedItem(int Index, string Reason);

}
