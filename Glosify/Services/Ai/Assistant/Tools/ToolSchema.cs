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

    internal static object StringProp(string description) =>
        new Dictionary<string, object>
        {
            ["type"] = "string",
            ["description"] = description,
        };

    internal static object IntegerProp(string description) =>
        new Dictionary<string, object>
        {
            ["type"] = "integer",
            ["description"] = description,
        };

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
