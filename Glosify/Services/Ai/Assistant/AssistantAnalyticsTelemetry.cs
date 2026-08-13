using System.Diagnostics;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;

namespace Glosify.Services.Ai.Assistant;

internal static class AssistantAnalyticsTelemetry
{
    internal static Activity? StartTurn(
        Guid turnId,
        Guid threadId,
        Guid? subjectId,
        string profile,
        string? requestedModel)
    {
        var activity = GenerativeAiTelemetry.ActivitySource.StartActivity(
            "assistant.turn",
            ActivityKind.Internal);
        activity?.SetTag("gen_ai.operation.name", "invoke_agent");
        activity?.SetTag("assistant.turn.id", turnId.ToString());
        activity?.SetTag("assistant.thread.id", threadId.ToString());
        activity?.SetTag("assistant.profile", profile);
        activity?.SetTag("gen_ai.request.model", requestedModel);
        if (subjectId.HasValue)
        {
            activity?.SetTag("enduser.pseudo.id", subjectId.Value.ToString());
        }
        return activity;
    }

    internal static Activity? StartInvocation(
        Guid turnId,
        Guid invocationId,
        int sequence,
        string profile,
        string? requestedModel)
    {
        var activity = GenerativeAiTelemetry.ActivitySource.StartActivity(
            "assistant.model.invoke",
            ActivityKind.Client);
        activity?.SetTag("gen_ai.operation.name", "chat");
        activity?.SetTag("assistant.turn.id", turnId.ToString());
        activity?.SetTag("assistant.invocation.id", invocationId.ToString());
        activity?.SetTag("assistant.invocation.sequence", sequence);
        activity?.SetTag("assistant.profile", profile);
        activity?.SetTag("gen_ai.request.model", requestedModel);
        return activity;
    }

    internal static Activity? StartTool(Guid turnId, Guid invocationId, string toolName)
    {
        var activity = GenerativeAiTelemetry.ActivitySource.StartActivity(
            $"execute_tool {toolName}",
            ActivityKind.Internal);
        activity?.SetTag("gen_ai.operation.name", "execute_tool");
        activity?.SetTag("gen_ai.tool.name", toolName);
        activity?.SetTag("assistant.turn.id", turnId.ToString());
        activity?.SetTag("assistant.invocation.id", invocationId.ToString());
        return activity;
    }

    /// <summary>
    /// Numbers describing how a book search went, tagged onto the ambient execute_tool
    /// span.
    /// </summary>
    /// <remarks>
    /// Deliberately counts only. Whether retrieval is working is a question about
    /// match_count, not about what the user asked, so nothing here needs the query text —
    /// which means this survives <c>AssistantAnalytics:CaptureContent</c> being off and
    /// does not widen what the telemetry knows about a user.
    /// </remarks>
    internal static void RecordBookSearch(
        int termCount,
        int matchCount,
        int returnedCount,
        int zeroPageTerms,
        int topPageHits)
    {
        var activity = Activity.Current;
        activity?.SetTag("assistant.search.term_count", termCount);
        activity?.SetTag("assistant.search.match_count", matchCount);
        activity?.SetTag("assistant.search.returned_count", returnedCount);
        activity?.SetTag("assistant.search.zero_page_terms", zeroPageTerms);
        activity?.SetTag("assistant.search.top_page_hits", topPageHits);
    }

    internal static void CompleteInvocation(Activity? activity, AgentTurnResult result)
    {
        var metadata = result.Metadata;
        activity?.SetTag("gen_ai.provider.name", metadata?.Provider);
        activity?.SetTag("gen_ai.response.model", metadata?.Model);
        activity?.SetTag("gen_ai.response.id", metadata?.ResponseId);
        activity?.SetTag("gen_ai.agent.name", metadata?.AgentName);
        activity?.SetTag("gen_ai.agent.version", metadata?.AgentVersion);
        activity?.SetTag("gen_ai.usage.input_tokens", metadata?.Usage.PromptTokens);
        activity?.SetTag("gen_ai.usage.output_tokens", metadata?.Usage.CandidateTokens);
        activity?.SetTag("gen_ai.usage.total_tokens", metadata?.Usage.TotalTokens);
        activity?.SetTag("assistant.outcome", "success");
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    internal static void Fail(Activity? activity, Exception exception)
    {
        activity?.SetTag("error.type", exception.GetType().FullName);
        activity?.SetTag("assistant.outcome", AssistantAnalyticsErrors.Classify(exception));
        activity?.SetStatus(ActivityStatusCode.Error);
    }

    internal static void RecordFeedback(Guid turnId, string traceId, string rating, IReadOnlyCollection<string> reasons)
    {
        using var activity = GenerativeAiTelemetry.ActivitySource.StartActivity(
            "gen_ai.evaluation.result",
            ActivityKind.Internal,
            TryParseParent(traceId));
        activity?.SetTag("gen_ai.evaluation.name", "user_feedback");
        activity?.SetTag("assistant.turn.id", turnId.ToString());
        activity?.SetTag("gen_ai.evaluation.score.value", rating == AssistantFeedbackRating.Up ? 1 : 0);
        activity?.SetTag("gen_ai.evaluation.score.label", rating);
        activity?.SetTag("assistant.feedback.reasons", string.Join(',', reasons));
        activity?.AddEvent(new ActivityEvent(
            "gen_ai.evaluation.result",
            tags: new ActivityTagsCollection
            {
                ["gen_ai.evaluation.name"] = "user_feedback",
                ["gen_ai.evaluation.score.value"] = rating == AssistantFeedbackRating.Up ? 1 : 0,
                ["gen_ai.evaluation.score.label"] = rating,
                ["assistant.turn.id"] = turnId.ToString(),
                ["assistant.feedback.reasons"] = string.Join(',', reasons),
            }));
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    private static ActivityContext TryParseParent(string traceId)
    {
        if (traceId.Length != 32)
        {
            return default;
        }

        try
        {
            return new ActivityContext(
                ActivityTraceId.CreateFromString(traceId.AsSpan()),
                ActivitySpanId.CreateRandom(),
                ActivityTraceFlags.Recorded);
        }
        catch (ArgumentOutOfRangeException)
        {
            return default;
        }
    }
}
