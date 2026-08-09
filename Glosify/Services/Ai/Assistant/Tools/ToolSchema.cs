using Glosify.Models.CustomQuizzes;
using Glosify.Services.Ai.Generation;

namespace Glosify.Services.Ai.Assistant.Tools;

/// <summary>
/// The small DSL each tool uses to declare its JSON parameter schema.
/// </summary>
/// <remarks>
/// Moved verbatim out of the 2,991-line AssistantTools so the per-tool classes can share it.
/// </remarks>
internal static class ToolSchema
{
    internal static object BuildSchema(Dictionary<string, object> properties, IReadOnlyList<string>? required = null)
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

    internal static AgentToolDeclaration AtomicElementDeclaration(
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

    internal static object EnumProp(string description, params object[] values) =>
        new Dictionary<string, object>
        {
            ["type"] = values.Length > 0 && values[0] is int ? "integer" : "string",
            ["enum"] = values,
            ["description"] = description,
        };

    internal static object StringArrayProp(string description) =>
        new Dictionary<string, object>
        {
            ["type"] = "array",
            ["description"] = description,
            ["items"] = new Dictionary<string, object> { ["type"] = "string" },
        };

    internal static object FlexibleBindingProp() =>
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

    internal static object FlexibleOptionsProp() =>
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

    internal static object StringProp(string description) =>
        new Dictionary<string, object>
        {
            ["type"] = "string",
            ["description"] = description,
        };

    internal static object BoolProp(string description) =>
        new Dictionary<string, object>
        {
            ["type"] = "boolean",
            ["description"] = description,
        };

    internal static object IntegerProp(string description) =>
        new Dictionary<string, object>
        {
            ["type"] = "integer",
            ["description"] = description,
        };

    internal static object CustomQuizBlocksProp(bool useWordReference) =>
        new Dictionary<string, object>
        {
            ["type"] = "array",
            ["description"] = "Custom quiz elements in display order. A playable quiz needs exactly one submit_button, exactly one feedback_message, and at least one answer element. Every answer element must have a non-empty, learner-visible label; when there are multiple answers, their labels must be distinct questions.",
            ["items"] = CustomQuizBlockProp(useWordReference, requireType: true),
        };

    internal static object CustomQuizBlockProp(bool useWordReference, bool requireType)
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

    internal static object WordArrayProp(string description) =>
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

    internal static object SentenceArrayProp(string description) =>
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
}
