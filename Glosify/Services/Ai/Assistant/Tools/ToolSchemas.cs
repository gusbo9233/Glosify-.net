using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Glosify.Services.Ai.Assistant.Tools;

/// <summary>
/// Derives each tool's JSON schema from its argument record and parses calls against it.
/// </summary>
/// <remarks>
/// The schema and the parser come from the same serializer options, so the model is offered
/// exactly the shape the tool accepts. Schemas use OpenAI strict mode: every property is
/// required, optional values are nullable, and unknown properties are refused, which lets
/// the provider constrain generation to valid arguments instead of relying on retries.
/// </remarks>
internal static class ToolSchemas
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
    };

    private static readonly JsonSchemaExporterOptions ExporterOptions = new()
    {
        TreatNullObliviousAsNonNullable = true,
        TransformSchemaNode = AddDescription,
    };

    private static readonly ConcurrentDictionary<Type, JsonElement> Cache = new();

    public static JsonElement For<T>() => Cache.GetOrAdd(typeof(T), static type =>
    {
        var node = JsonSchemaExporter.GetJsonSchemaAsNode(Options, type, ExporterOptions);
        MakeStrict(node);
        return JsonSerializer.SerializeToElement(node);
    });

    public static bool TryParse<T>(string json, out T value, out string problem) where T : class
    {
        value = null!;
        problem = string.Empty;
        try
        {
            var parsed = JsonSerializer.Deserialize<T>(string.IsNullOrWhiteSpace(json) ? "{}" : json, Options);
            if (parsed is null)
            {
                problem = "Arguments must be a JSON object.";
                return false;
            }

            value = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            problem = ex.Message;
            return false;
        }
    }

    private static JsonNode AddDescription(JsonSchemaExporterContext context, JsonNode schema)
    {
        var description = Describe(context.PropertyInfo?.AttributeProvider)
            ?? Describe(context.PropertyInfo?.AssociatedParameter?.AttributeProvider);
        if (description is null)
        {
            return schema;
        }

        if (schema is not JsonObject obj)
        {
            // A boolean "true" schema: wrap it so it can carry a description.
            obj = new JsonObject();
        }

        obj.Remove("description");
        obj.Insert(0, "description", description);
        return obj;
    }

    private static string? Describe(ICustomAttributeProvider? provider) =>
        provider?.GetCustomAttributes(typeof(DescriptionAttribute), inherit: true)
            .OfType<DescriptionAttribute>()
            .FirstOrDefault()
            ?.Description;

    private static void MakeStrict(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                obj.Remove("$schema");
                // Strict mode wants an explicit type; the exporter leaves string enums untyped.
                if (obj["enum"] is JsonArray values && obj["type"] is null)
                {
                    obj["type"] = values.Any(value => value is null)
                        ? new JsonArray("string", "null")
                        : "string";
                }

                if (obj["properties"] is JsonObject properties)
                {
                    obj["required"] = new JsonArray(properties.Select(property => (JsonNode)property.Key).ToArray());
                    obj["additionalProperties"] = false;
                    foreach (var (_, property) in properties)
                    {
                        MakeStrict(property);
                    }
                }

                MakeStrict(obj["items"]);
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    MakeStrict(item);
                }

                break;
        }
    }
}
