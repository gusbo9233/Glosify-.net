using System.Text.Json;
using System.Text.Json.Nodes;
using Glosify.Services.Ai.Generation;

namespace Glosify.Services.Ai.Assistant.Runtime;

internal static class RuntimeToolSchema
{
    internal static AgentToolDeclaration Strict(AgentToolDeclaration tool)
    {
        var schema = JsonSerializer.SerializeToNode(tool.ParametersJsonSchema, RuntimeJson.Options)!;
        Normalize(schema);
        return tool with { ParametersJsonSchema = schema, Strict = true };
    }
    private static void Normalize(JsonNode node)
    {
        if (node is not JsonObject obj) return;
        if (obj["properties"] is JsonObject properties)
        {
            var required = obj["required"]?.AsArray().Select(x => x!.GetValue<string>()).ToHashSet() ?? [];
            foreach (var (name, property) in properties)
            {
                if (property is not JsonObject field) continue;
                Normalize(field);
                if (!required.Contains(name) && field["type"] is JsonValue type)
                {
                    field["type"] = new JsonArray(type.GetValue<string>(), "null");
                    if (field["enum"] is JsonArray values && !values.Any(x => x is null)) values.Add((JsonNode?)null);
                }
            }
            obj["required"] = new JsonArray(properties.Select(x => JsonValue.Create(x.Key)).ToArray());
            obj["additionalProperties"] = false;
        }
        if (obj["items"] is JsonObject items) Normalize(items);
    }
    internal static string? Validate(JsonElement value, JsonElement schema, string path = "arguments")
    {
        if (schema.TryGetProperty("type", out var type))
        {
            var types = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(x => x.GetString()) : [type.GetString()];
            bool Match(string? name) => name switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "number" => value.ValueKind == JsonValueKind.Number,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "null" => value.ValueKind == JsonValueKind.Null,
                _ => false,
            };
            if (!types.Any(Match)) return $"{path} has the wrong JSON type.";
        }
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (schema.TryGetProperty("enum", out var values) && !values.EnumerateArray().Any(x => x.GetRawText() == value.GetRawText()))
            return $"{path} is not one of the allowed values.";
        if (value.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var properties))
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var name in required.EnumerateArray())
                    if (!value.TryGetProperty(name.GetString()!, out _)) return $"{path}.{name.GetString()} is required (use null for an optional field).";
            foreach (var property in value.EnumerateObject())
            {
                if (!properties.TryGetProperty(property.Name, out var child)) return $"{path}.{property.Name} is not a declared argument.";
                if (Validate(property.Value, child, path + "." + property.Name) is { } error) return error;
            }
        }
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
            foreach (var item in value.EnumerateArray()) if (Validate(item, items, path + "[]") is { } error) return error;
        return null;
    }
}
