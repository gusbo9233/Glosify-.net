using System.Diagnostics;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant;

internal sealed class AssistantAnalyticsStore
{
    private readonly IDbContextFactory<GlosifyContext>? _contextFactory;
    private readonly GlosifyContext? _sharedContext;
    private readonly TimeProvider _timeProvider;

    public AssistantAnalyticsStore(
        IDbContextFactory<GlosifyContext> contextFactory,
        TimeProvider timeProvider)
    {
        _contextFactory = contextFactory;
        _timeProvider = timeProvider;
    }

    private AssistantAnalyticsStore(GlosifyContext sharedContext, TimeProvider timeProvider)
    {
        _sharedContext = sharedContext;
        _timeProvider = timeProvider;
    }

    internal static AssistantAnalyticsStore ForSharedTestContext(
        GlosifyContext context,
        TimeProvider timeProvider) => new(context, timeProvider);

    public async Task StartInvocationAsync(
        AssistantModelInvocation invocation,
        CancellationToken cancellationToken)
    {
        await UseContextAsync(async context =>
        {
            context.AssistantModelInvocations.Add(invocation);
            await context.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task CompleteInvocationAsync(
        Guid invocationId,
        AgentTurnResult result,
        double durationMs,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        await UseContextAsync(async context =>
        {
            var invocation = await context.AssistantModelInvocations
                .SingleAsync(candidate => candidate.Id == invocationId, cancellationToken);
            var metadata = result.Metadata;
            invocation.Status = AssistantInvocationStatus.Completed;
            invocation.CompletedAt = _timeProvider.GetUtcNow();
            invocation.DurationMs = durationMs;
            invocation.ResponseJson = AssistantAnalyticsJson.Serialize(result);
            invocation.RequestJson = string.IsNullOrWhiteSpace(metadata?.EffectiveRequestJson)
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
            await context.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task FailInvocationAsync(
        Guid invocationId,
        Exception exception,
        double durationMs,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        await UseContextAsync(async context =>
        {
            var invocation = await context.AssistantModelInvocations
                .SingleAsync(candidate => candidate.Id == invocationId, cancellationToken);
            invocation.Status = exception is OperationCanceledException
                ? AssistantInvocationStatus.Cancelled
                : AssistantInvocationStatus.Failed;
            invocation.ErrorCategory = AssistantAnalyticsErrors.Classify(exception);
            invocation.CompletedAt = _timeProvider.GetUtcNow();
            invocation.DurationMs = durationMs;
            invocation.TraceId = activity?.TraceId.ToHexString() ?? invocation.TraceId;
            invocation.SpanId = activity?.SpanId.ToHexString() ?? invocation.SpanId;
            await context.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task StartToolAsync(AssistantToolExecution execution, CancellationToken cancellationToken)
    {
        await UseContextAsync(async context =>
        {
            context.AssistantToolExecutions.Add(execution);
            await context.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task CompleteToolAsync(
        Guid executionId,
        string resultJson,
        int proposedChangeCount,
        double durationMs,
        CancellationToken cancellationToken)
    {
        await UseContextAsync(async context =>
        {
            var execution = await context.AssistantToolExecutions
                .SingleAsync(candidate => candidate.Id == executionId, cancellationToken);
            execution.Status = AssistantInvocationStatus.Completed;
            execution.ResultJson = resultJson;
            execution.ProposedChangeCount = proposedChangeCount;
            execution.DurationMs = durationMs;
            execution.CompletedAt = _timeProvider.GetUtcNow();
            await context.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task FailToolAsync(
        Guid executionId,
        Exception exception,
        double durationMs,
        CancellationToken cancellationToken)
    {
        await UseContextAsync(async context =>
        {
            var execution = await context.AssistantToolExecutions
                .SingleAsync(candidate => candidate.Id == executionId, cancellationToken);
            execution.Status = exception is OperationCanceledException
                ? AssistantInvocationStatus.Cancelled
                : AssistantInvocationStatus.Failed;
            execution.ErrorCategory = AssistantAnalyticsErrors.Classify(exception);
            execution.DurationMs = durationMs;
            execution.CompletedAt = _timeProvider.GetUtcNow();
            await context.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    private async Task UseContextAsync(
        Func<GlosifyContext, Task> action,
        CancellationToken cancellationToken)
    {
        if (_sharedContext is not null)
        {
            await action(_sharedContext);
            return;
        }

        await using var context = await _contextFactory!.CreateDbContextAsync(cancellationToken);
        await action(context);
    }
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
            Redact(node);
            return node?.ToJsonString(Options) ?? json;
        }
        catch (System.Text.Json.JsonException)
        {
            return json;
        }
    }

    private static void Redact(System.Text.Json.Nodes.JsonNode? node)
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
                    Redact(property.Value);
                }
            }
        }
        else if (node is System.Text.Json.Nodes.JsonArray arrayNode)
        {
            foreach (var item in arrayNode)
            {
                Redact(item);
            }
        }
    }
}
