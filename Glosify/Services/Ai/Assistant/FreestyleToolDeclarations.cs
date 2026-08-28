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
                "Propose creating a prompt-and-answer quiz.",
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
            "list_books" => new(
                "list_books",
                "List uploaded source books with title, page count, date, and id.",
                BuildSchema(new Dictionary<string, object>
                {
                    ["offset"] = IntegerProp("Optional number of books to skip. Defaults to 0."),
                })),
            _ => declaration,
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
