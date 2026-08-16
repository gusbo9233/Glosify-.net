using Glosify.Services.Ai.Generation;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant;

/// <summary>
/// Presents the existing quiz handlers as prompt-and-answer tools in Freestyle mode.
/// The dispatcher aliases keep persistence compatible while the model sees no
/// language-learning contract.
/// </summary>
internal static class FreestyleToolDeclarations
{
    public static AgentToolDeclaration For(AgentToolDeclaration declaration) =>
        declaration.Name switch
        {
            "list_words" => new(
                "list_items",
                "List the prompt-and-answer items in the current quiz. Use this before proposing changes.",
                BuildSchema(new Dictionary<string, object>
                {
                    ["offset"] = IntegerProp("Optional number of items to skip. Defaults to 0."),
                })),
            "search_words" => new(
                "search_items",
                "Search prompts and answers in the current quiz.",
                BuildSchema(new Dictionary<string, object>
                {
                    ["query"] = StringProp("Text to find in a prompt or answer."),
                    ["limit"] = IntegerProp("Optional maximum number of matches, from 1 to 50."),
                }, required: ["query"])),
            "get_word" => new(
                "get_item",
                "Get one prompt-and-answer item by id.",
                BuildSchema(new Dictionary<string, object>
                {
                    ["item_id"] = StringProp("Id of the item to fetch."),
                }, required: ["item_id"])),
            "get_quiz_summary" => new(
                "get_quiz_overview",
                "Get the current quiz name, collection, visibility, and item count.",
                BuildSchema([])),
            "add_word" => new(
                "add_item",
                "Propose adding one prompt-and-answer item. The user reviews it before it is saved.",
                BuildSchema(new Dictionary<string, object>
                {
                    ["prompt"] = StringProp("Question, term, or cue shown first."),
                    ["answer"] = StringProp("Correct answer, explanation, or definition."),
                }, required: ["prompt", "answer"])),
            "add_words" => new(
                "add_items",
                "Propose adding multiple prompt-and-answer items in one call.",
                BuildSchema(new Dictionary<string, object>
                {
                    ["items"] = ItemArray("Items to add."),
                }, required: ["items"])),
            "edit_word" => new(
                "edit_item",
                "Propose changing an item's prompt and/or answer.",
                BuildSchema(new Dictionary<string, object>
                {
                    ["item_id"] = StringProp("Id of the item to edit."),
                    ["prompt"] = StringProp("Optional replacement prompt."),
                    ["answer"] = StringProp("Optional replacement answer."),
                }, required: ["item_id"])),
            "edit_words" => new(
                "edit_items",
                "Propose changing multiple prompt-and-answer items in one call.",
                BuildSchema(new Dictionary<string, object>
                {
                    ["changes"] = new Dictionary<string, object>
                    {
                        ["type"] = "array",
                        ["items"] = new Dictionary<string, object>
                        {
                            ["type"] = "object",
                            ["properties"] = new Dictionary<string, object>
                            {
                                ["item_id"] = StringProp("Id of the item to edit."),
                                ["prompt"] = StringProp("Optional replacement prompt."),
                                ["answer"] = StringProp("Optional replacement answer."),
                            },
                            ["required"] = new[] { "item_id" },
                        },
                    },
                }, required: ["changes"])),
            "delete_word" => new(
                "delete_item",
                "Propose removing an item from the quiz.",
                BuildSchema(new Dictionary<string, object>
                {
                    ["item_id"] = StringProp("Id of the item to remove."),
                }, required: ["item_id"])),
            "create_vocabulary_quiz" => new(
                "create_quiz",
                "Propose creating a standard prompt-and-answer quiz. Use a custom quiz for interactive formats.",
                BuildSchema(new Dictionary<string, object>
                {
                    ["name"] = StringProp("Quiz name."),
                    ["collection_id"] = StringProp("Optional destination collection id."),
                    ["items"] = ItemArray("Optional starter items."),
                }, required: ["name"])),
            "list_collections" => new(
                "list_collections",
                "List the user's Freestyle collections.",
                BuildSchema([])),
            "list_quizzes" => new(
                "list_quizzes",
                "List the user's Freestyle quizzes to find existing work and avoid duplicates.",
                BuildSchema([])),
            "create_collection" => new(
                "create_collection",
                "Propose creating a collection for related quizzes.",
                BuildSchema(new Dictionary<string, object>
                {
                    ["name"] = StringProp("Collection name."),
                    ["parent_collection_id"] = StringProp("Optional parent collection id."),
                }, required: ["name"])),
            "create_custom_quiz_from_content" => new(
                "create_custom_quiz_from_content",
                "Create backing quiz shells and starter items from source material, then add each interactive element in the same turn.",
                BuildSchema(new Dictionary<string, object>
                {
                    ["quiz_name"] = StringProp("Backing quiz name."),
                    ["custom_quiz_name"] = StringProp("Interactive quiz name."),
                    ["collection_id"] = StringProp("Optional destination collection id."),
                    ["template_id"] = StringProp("Optional visual template id."),
                    ["items"] = ItemArray("Starter prompt-and-answer items available for bindings."),
                }, required: ["quiz_name", "custom_quiz_name", "items"])),
            "list_books" => new(
                "list_books",
                "List uploaded source books with title, page count, date, and id.",
                BuildSchema(new Dictionary<string, object>
                {
                    ["offset"] = IntegerProp("Optional number of books to skip. Defaults to 0."),
                })),
            "add_label" => new(
                "add_label",
                "Add one visible label to the custom quiz. Call once per label.",
                AtomicElementSchema(new Dictionary<string, object>
                {
                    ["id"] = StringProp("Stable unique element id."),
                    ["text"] = StringProp("Visible label text."),
                    ["label_type"] = EnumProp("Label type.", "instruction_label", "prompt_label", "quiz_heading"),
                }, ["id", "text"])),
            "add_text_input" => new(
                "add_text_input",
                "Add one graded written answer to the custom quiz. Use expected_text for a literal answer or expected_binding for an existing item.",
                AtomicElementSchema(new Dictionary<string, object>
                {
                    ["id"] = StringProp("Stable unique element id."),
                    ["label"] = StringProp("Complete question or exercise cue. Put {{blank}} where a compact answer field belongs."),
                    ["answer_type"] = EnumProp("Use text_input for one line or textarea for a long answer.", "text_input", "textarea"),
                    ["expected_text"] = StringProp("Literal correct answer."),
                    ["expected_binding"] = ItemBindingProp(),
                }, ["id", "label"])),
            "add_choice" => new(
                "add_choice",
                "Add one graded choice question with at least two options and the correct selection or selections marked.",
                AtomicElementSchema(new Dictionary<string, object>
                {
                    ["id"] = StringProp("Stable unique element id."),
                    ["label"] = StringProp("Specific question shown to the learner."),
                    ["choice_type"] = EnumProp("Choice control type.", "radio_group", "multi_select_group", "select_menu"),
                    ["options"] = ItemOptionsProp(),
                }, ["id", "label", "choice_type", "options"])),
            "add_custom_quiz_element" => new(
                "add_custom_quiz_element",
                "Add exactly one custom quiz element not covered by a more specific tool.",
                BuildSchema(new Dictionary<string, object>
                {
                    ["custom_quiz_id"] = StringProp("Optional existing custom quiz id."),
                    ["element"] = OpenObject("One complete element with a stable id and type."),
                }, required: ["element"])),
            "configure_custom_quiz_element" => new(
                "configure_custom_quiz_element",
                "Propose changing supplied settings on one existing custom quiz element.",
                BuildSchema(new Dictionary<string, object>
                {
                    ["custom_quiz_id"] = StringProp("Optional existing custom quiz id."),
                    ["block_id"] = StringProp("Existing element id from get_custom_quiz."),
                    ["settings"] = OpenObject("Only the element settings to change."),
                }, required: ["block_id", "settings"])),
            _ => declaration,
        };

    private static object OpenObject(string description) => new Dictionary<string, object>
    {
        ["type"] = "object",
        ["description"] = description,
    };

    private static object AtomicElementSchema(Dictionary<string, object> properties, IReadOnlyList<string> required)
    {
        properties["custom_quiz_id"] = StringProp("Optional existing custom quiz id. Omit for the open quiz or a quiz started earlier in this turn.");
        properties["column_span"] = EnumProp("Element width in the 12-column layout.", 3, 4, 6, 12);
        properties["grid_column"] = IntegerProp("Optional start column from 1 to 12.");
        properties["grid_row"] = IntegerProp("Optional row from 1 to 500.");
        return BuildSchema(properties, required);
    }

    private static object ItemBindingProp() => new Dictionary<string, object>
    {
        ["type"] = "object",
        ["description"] = "Expected item binding.",
        ["properties"] = new Dictionary<string, object>
        {
            ["item_id"] = StringProp("Existing backing-quiz item id."),
            ["item_prompt"] = StringProp("Exact starter prompt when the backing quiz is pending creation."),
            ["field"] = EnumProp("Item side to expect.", "prompt", "answer"),
        },
        ["required"] = new[] { "field" },
    };

    private static object ItemOptionsProp() => new Dictionary<string, object>
    {
        ["type"] = "array",
        ["items"] = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["id"] = StringProp("Stable unique option id."),
                ["binding"] = ItemBindingProp(),
                ["is_correct"] = new Dictionary<string, object> { ["type"] = "boolean" },
            },
            ["required"] = new[] { "id", "binding", "is_correct" },
        },
    };

    private static Dictionary<string, object> ItemArray(string description) => new()
    {
        ["type"] = "array",
        ["description"] = description,
        ["items"] = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["prompt"] = StringProp("Question, term, or cue."),
                ["answer"] = StringProp("Correct answer, explanation, or definition."),
            },
            ["required"] = new[] { "prompt", "answer" },
        },
    };
}
