using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Glosify.Services.Ai.Generation;

internal static class FoundryMessageMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static IReadOnlyList<ChatMessage> MapHistory(IReadOnlyList<AgentTurn> turns)
    {
        var messages = new List<ChatMessage>(turns.Count);
        var pendingCallIds = new Dictionary<string, Queue<string>>(StringComparer.Ordinal);

        for (var turnIndex = 0; turnIndex < turns.Count; turnIndex++)
        {
            var turn = turns[turnIndex];
            var stored = DeserializeContent(turn.ContentJson);
            var contents = new List<AIContent>();

            for (var partIndex = 0; partIndex < stored.Parts.Count; partIndex++)
            {
                var part = stored.Parts[partIndex];
                switch (part.Kind)
                {
                    case "text" when part.Text is not null:
                        contents.Add(new TextContent(part.Text));
                        break;
                    case "function_call":
                    {
                        var name = part.Name ?? string.Empty;
                        var callId = string.IsNullOrWhiteSpace(part.CallId)
                            ? $"legacy-call-{turnIndex}-{partIndex}"
                            : part.CallId;
                        if (!pendingCallIds.TryGetValue(name, out var ids))
                        {
                            ids = new Queue<string>();
                            pendingCallIds[name] = ids;
                        }
                        ids.Enqueue(callId);
                        contents.Add(new FunctionCallContent(
                            callId,
                            name,
                            DeserializeArguments(part.ArgsJson)));
                        break;
                    }
                    case "function_response":
                    {
                        var name = part.Name ?? string.Empty;
                        var callId = part.CallId;
                        if (string.IsNullOrWhiteSpace(callId)
                            && pendingCallIds.TryGetValue(name, out var ids)
                            && ids.Count > 0)
                        {
                            callId = ids.Dequeue();
                        }
                        callId ??= $"legacy-result-{turnIndex}-{partIndex}";
                        contents.Add(new FunctionResultContent(
                            callId,
                            DeserializeResult(part.ResponseJson)));
                        break;
                    }
                }
            }

            if (contents.Count == 0)
            {
                contents.Add(new TextContent(string.Empty));
            }

            var role = contents.All(content => content is FunctionResultContent)
                ? ChatRole.Tool
                : MapRole(turn.Role);
            messages.Add(new ChatMessage(role, contents));
        }

        return messages;
    }

    internal static IReadOnlyList<AITool> MapTools(IReadOnlyList<AgentToolDeclaration> declarations)
    {
        return declarations
            .Select(declaration =>
            {
                var schema = JsonSerializer.SerializeToElement(
                    declaration.ParametersJsonSchema,
                    JsonOptions);
                return (AITool)AIFunctionFactory.CreateDeclaration(
                    declaration.Name,
                    declaration.Description,
                    schema,
                    returnJsonSchema: null);
            })
            .ToArray();
    }

    private static ChatRole MapRole(string role) =>
        role.ToLowerInvariant() switch
        {
            "model" or "assistant" => ChatRole.Assistant,
            "system" => ChatRole.System,
            "tool" => ChatRole.Tool,
            _ => ChatRole.User,
        };

    private static StoredContent DeserializeContent(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<StoredContent>(json, JsonOptions) ?? new StoredContent();
        }
        catch (JsonException)
        {
            return new StoredContent();
        }
    }

    private static IDictionary<string, object?> DeserializeArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, object?>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json, JsonOptions)
                ?? new Dictionary<string, object?>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>();
        }
    }

    private static object DeserializeResult(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new { };
        }

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private sealed class StoredContent
    {
        public List<StoredPart> Parts { get; set; } = [];
    }

    private sealed class StoredPart
    {
        public string Kind { get; set; } = "text";
        public string? Text { get; set; }
        public string? Name { get; set; }
        public string? ArgsJson { get; set; }
        public string? ResponseJson { get; set; }
        public string? CallId { get; set; }
    }
}
