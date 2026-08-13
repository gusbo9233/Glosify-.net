using System.Diagnostics;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Ai.Assistant;

internal sealed class AssistantAnalyticsStore
{
    private readonly IAssistantAnalyticsBatchWriter _writer;
    private readonly TimeProvider _timeProvider;
    private readonly bool _captureContent;

    public AssistantAnalyticsStore(
        IAssistantAnalyticsBatchWriter writer,
        TimeProvider timeProvider,
        IOptions<AssistantAnalyticsOptions> options)
    {
        _writer = writer;
        _timeProvider = timeProvider;
        _captureContent = options.Value.CaptureContent;
    }

    /// <summary>
    /// Whether request and response bodies are stored. Callers use this to skip composing
    /// payloads this store would discard.
    /// </summary>
    public bool CaptureContent => _captureContent;

    public void CompleteInvocation(
        AssistantModelInvocation invocation,
        AgentTurnResult result,
        double durationMs,
        Activity? activity)
    {
        var metadata = result.Metadata;
        invocation.Status = AssistantInvocationStatus.Completed;
        invocation.CompletedAt = _timeProvider.GetUtcNow();
        invocation.DurationMs = durationMs;
        invocation.ResponseJson = SerializePayload(result);
        invocation.RequestJson = !_captureContent || string.IsNullOrWhiteSpace(metadata?.EffectiveRequestJson)
            ? invocation.RequestJson
            : AssistantAnalyticsJson.RedactSecrets(metadata.EffectiveRequestJson);
        invocation.Provider = metadata?.Provider ?? invocation.Provider;
        invocation.ActualModel = metadata?.Model ?? invocation.ActualModel;
        invocation.AgentName = metadata?.AgentName ?? invocation.AgentName;
        invocation.AgentVersion = metadata?.AgentVersion ?? invocation.AgentVersion;
        invocation.ProviderResponseId = metadata?.ResponseId;
        invocation.PromptTokens = metadata?.Usage.PromptTokens;
        invocation.CandidateTokens = metadata?.Usage.CandidateTokens;
        invocation.ThoughtTokens = metadata?.Usage.ThoughtTokens;
        invocation.ToolPromptTokens = metadata?.Usage.ToolPromptTokens;
        invocation.TotalTokens = metadata?.Usage.TotalTokens;
        invocation.TraceId = activity?.TraceId.ToHexString() ?? invocation.TraceId;
        invocation.SpanId = activity?.SpanId.ToHexString() ?? invocation.SpanId;
    }

    public void FailInvocation(
        AssistantModelInvocation invocation,
        Exception exception,
        double durationMs,
        Activity? activity)
    {
        invocation.Status = exception is OperationCanceledException
            ? AssistantInvocationStatus.Cancelled
            : AssistantInvocationStatus.Failed;
        invocation.ErrorCategory = AssistantAnalyticsErrors.Classify(exception);
        invocation.CompletedAt = _timeProvider.GetUtcNow();
        invocation.DurationMs = durationMs;
        invocation.TraceId = activity?.TraceId.ToHexString() ?? invocation.TraceId;
        invocation.SpanId = activity?.SpanId.ToHexString() ?? invocation.SpanId;
    }

    public void CompleteTool(
        AssistantToolExecution execution,
        string resultJson,
        int proposedChangeCount,
        double durationMs)
    {
        execution.Status = AssistantInvocationStatus.Completed;
        execution.ResultJson = StoreJson(resultJson);
        execution.ProposedChangeCount = proposedChangeCount;
        execution.DurationMs = durationMs;
        execution.CompletedAt = _timeProvider.GetUtcNow();
    }

    public void FailTool(
        AssistantToolExecution execution,
        Exception exception,
        double durationMs)
    {
        execution.Status = exception is OperationCanceledException
            ? AssistantInvocationStatus.Cancelled
            : AssistantInvocationStatus.Failed;
        execution.ErrorCategory = AssistantAnalyticsErrors.Classify(exception);
        execution.DurationMs = durationMs;
        execution.CompletedAt = _timeProvider.GetUtcNow();
    }

    public ValueTask SubmitBatchAsync(
        IReadOnlyCollection<AssistantModelInvocation> invocations,
        IReadOnlyCollection<AssistantToolExecution> executions,
        CancellationToken cancellationToken) =>
        _writer.SubmitAsync(invocations, executions, cancellationToken);

    public string SerializePayload<T>(T value) =>
        _captureContent ? AssistantAnalyticsJson.Serialize(value) : "{}";

    public string StoreJson(string json) =>
        _captureContent ? AssistantAnalyticsJson.RedactSecrets(json) : "{}";
}

internal static class AssistantAnalyticsErrors
{
    internal static string Classify(Exception exception) => exception switch
    {
        OperationCanceledException => "cancelled",
        GenerativeAiTimeoutException => "timeout",
        GenerativeAiDependencyUnavailableException => "dependency_unavailable",
        GenerativeAiValidationException => "validation_error",
        GenerativeAiStructuredOutputException => "structured_output_error",
        InsufficientAiCreditsException => "insufficient_credits",
        MonthlyAiBudgetExceededException => "monthly_budget_exceeded",
        _ => "unhandled_error",
    };
}

internal static class AssistantAnalyticsJson
{
    private static readonly System.Text.Json.JsonSerializerOptions Options =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    private static readonly HashSet<string> SecretPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "password", "passwd", "secret", "clientsecret", "apikey",
        "accesstoken", "refreshtoken", "connectionstring", "sastoken", "credential",
    };

    internal static string Serialize<T>(T value) =>
        RedactSecrets(System.Text.Json.JsonSerializer.Serialize(value, Options));

    internal static string RedactSecrets(string json)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            node = Redact(node);
            return node?.ToJsonString(Options) ?? json;
        }
        catch (System.Text.Json.JsonException)
        {
            return json;
        }
    }

    private static System.Text.Json.Nodes.JsonNode? Redact(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is System.Text.Json.Nodes.JsonObject objectNode)
        {
            foreach (var property in objectNode.ToList())
            {
                var normalizedName = property.Key.Replace("_", string.Empty, StringComparison.Ordinal)
                    .Replace("-", string.Empty, StringComparison.Ordinal)
                    .Replace(".", string.Empty, StringComparison.Ordinal);
                if (SecretPropertyNames.Contains(normalizedName))
                {
                    objectNode[property.Key] = "[REDACTED]";
                }
                else
                {
                    var redacted = Redact(property.Value);
                    if (!ReferenceEquals(redacted, property.Value))
                    {
                        objectNode[property.Key] = redacted;
                    }
                }
            }
        }
        else if (node is System.Text.Json.Nodes.JsonArray arrayNode)
        {
            for (var index = 0; index < arrayNode.Count; index++)
            {
                var item = arrayNode[index];
                var redacted = Redact(item);
                if (!ReferenceEquals(redacted, item))
                {
                    arrayNode[index] = redacted;
                }
            }
        }
        else if (node is System.Text.Json.Nodes.JsonValue valueNode
            && valueNode.TryGetValue<string>(out var encodedJson)
            && LooksLikeJson(encodedJson))
        {
            try
            {
                var encodedNode = System.Text.Json.Nodes.JsonNode.Parse(encodedJson);
                encodedNode = Redact(encodedNode);
                return encodedNode is null
                    ? node
                    : System.Text.Json.Nodes.JsonValue.Create(encodedNode.ToJsonString(Options));
            }
            catch (System.Text.Json.JsonException)
            {
                return node;
            }
        }

        return node;
    }

    private static bool LooksLikeJson(string value)
    {
        var trimmed = value.AsSpan().TrimStart();
        return !trimmed.IsEmpty && trimmed[0] is '{' or '[';
    }
}
