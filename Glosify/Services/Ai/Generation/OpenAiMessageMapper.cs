#pragma warning disable OPENAI001

using System.Text.Json;
using OpenAI.Responses;

namespace Glosify.Services.Ai.Generation;

internal static class OpenAiMessageMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static void AddHistory(
        IList<ResponseItem> destination,
        IReadOnlyList<AgentTurn> turns)
    {
        var pendingCallIds = new Dictionary<string, Queue<string>>(StringComparer.Ordinal);
        for (var turnIndex = 0; turnIndex < turns.Count; turnIndex++)
        {
            var turn = turns[turnIndex];
            var stored = DeserializeContent(turn.ContentJson);
            var textParts = new List<string>();

            for (var partIndex = 0; partIndex < stored.Parts.Count; partIndex++)
            {
                var part = stored.Parts[partIndex];
                switch (part.Kind)
                {
                    case "text" when part.Text is not null:
                        textParts.Add(part.Text);
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
                        destination.Add(ResponseItem.CreateFunctionCallItem(
                            callId,
                            name,
                            BinaryData.FromString(NormalizeObjectJson(part.ArgsJson))));
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
                        destination.Add(ResponseItem.CreateFunctionCallOutputItem(
                            callId,
                            part.ResponseJson ?? "{}"));
                        break;
                    }
                }
            }

            if (textParts.Count > 0)
            {
                var text = string.Join("\n", textParts);
                destination.Add(MapText(turn.Role, text));
            }
        }
    }

    internal static ResponseTool MapTool(AgentToolDeclaration declaration) =>
        ResponseTool.CreateFunctionTool(
            declaration.Name,
            BinaryData.FromString(JsonSerializer.Serialize(
                declaration.ParametersJsonSchema,
                JsonOptions)),
            strictModeEnabled: false,
            declaration.Description);

    private static ResponseItem MapText(string role, string text) =>
        role.ToLowerInvariant() switch
        {
            "model" or "assistant" => ResponseItem.CreateAssistantMessageItem(text, []),
            "system" => ResponseItem.CreateSystemMessageItem(text),
            "developer" => ResponseItem.CreateDeveloperMessageItem(text),
            _ => ResponseItem.CreateUserMessageItem(text),
        };

    private static StoredContent DeserializeContent(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<StoredContent>(json, JsonOptions)
                ?? new StoredContent();
        }
        catch (JsonException)
        {
            return new StoredContent();
        }
    }

    private static string NormalizeObjectJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.GetRawText()
                : "{}";
        }
        catch (JsonException)
        {
            return "{}";
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

#pragma warning restore OPENAI001
