using System.ComponentModel.DataAnnotations.Schema;

namespace Glosify.Models.Entities;

[Table("assistant_turns")]
public sealed class AssistantTurn
{
    [Column("id")]
    public Guid Id { get; set; }

    [Column("thread_id")]
    public Guid ThreadId { get; set; }

    [Column("profile")]
    public string Profile { get; set; } = string.Empty;

    [Column("requested_model")]
    public string? RequestedModel { get; set; }

    [Column("provider")]
    public string? Provider { get; set; }

    [Column("actual_model")]
    public string? ActualModel { get; set; }

    [Column("status")]
    public string Status { get; set; } = AssistantTurnStatus.Started;

    [Column("error_category")]
    public string? ErrorCategory { get; set; }

    [Column("started_at")]
    public DateTimeOffset StartedAt { get; set; }

    [Column("completed_at")]
    public DateTimeOffset? CompletedAt { get; set; }

    [Column("server_duration_ms")]
    public double? ServerDurationMs { get; set; }

    [Column("client_duration_ms")]
    public double? ClientDurationMs { get; set; }

    [Column("tool_call_count")]
    public int ToolCallCount { get; set; }

    [Column("proposed_change_count")]
    public int ProposedChangeCount { get; set; }

    [Column("change_outcome")]
    public string? ChangeOutcome { get; set; }

    [Column("change_outcome_at")]
    public DateTimeOffset? ChangeOutcomeAt { get; set; }

    [Column("final_message_id")]
    public Guid? FinalMessageId { get; set; }

    [Column("trace_id")]
    public string? TraceId { get; set; }

    [Column("provider_response_id")]
    public string? ProviderResponseId { get; set; }
}

public static class AssistantTurnStatus
{
    public const string Started = "started";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public static class AssistantChangeOutcome
{
    public const string Proposed = "proposed";
    public const string Applied = "applied";
    public const string Rejected = "rejected";
}

[Table("assistant_model_invocations")]
public sealed class AssistantModelInvocation
{
    [Column("id")]
    public Guid Id { get; set; }

    [Column("turn_id")]
    public Guid TurnId { get; set; }

    [Column("sequence")]
    public int Sequence { get; set; }

    [Column("agent_name")]
    public string? AgentName { get; set; }

    [Column("agent_version")]
    public string? AgentVersion { get; set; }

    [Column("profile")]
    public string Profile { get; set; } = string.Empty;

    [Column("provider")]
    public string Provider { get; set; } = string.Empty;

    [Column("requested_model")]
    public string? RequestedModel { get; set; }

    [Column("actual_model")]
    public string? ActualModel { get; set; }

    [Column("request_json")]
    public string RequestJson { get; set; } = string.Empty;

    [Column("response_json")]
    public string? ResponseJson { get; set; }

    [Column("provider_response_id")]
    public string? ProviderResponseId { get; set; }

    [Column("status")]
    public string Status { get; set; } = AssistantInvocationStatus.Started;

    [Column("error_category")]
    public string? ErrorCategory { get; set; }

    [Column("started_at")]
    public DateTimeOffset StartedAt { get; set; }

    [Column("completed_at")]
    public DateTimeOffset? CompletedAt { get; set; }

    [Column("duration_ms")]
    public double? DurationMs { get; set; }

    [Column("prompt_tokens")]
    public int? PromptTokens { get; set; }

    [Column("candidate_tokens")]
    public int? CandidateTokens { get; set; }

    [Column("thought_tokens")]
    public int? ThoughtTokens { get; set; }

    [Column("tool_prompt_tokens")]
    public int? ToolPromptTokens { get; set; }

    [Column("total_tokens")]
    public int? TotalTokens { get; set; }

    [Column("trace_id")]
    public string? TraceId { get; set; }

    [Column("span_id")]
    public string? SpanId { get; set; }
}

public static class AssistantInvocationStatus
{
    public const string Started = "started";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

[Table("assistant_tool_executions")]
public sealed class AssistantToolExecution
{
    [Column("id")]
    public Guid Id { get; set; }

    [Column("turn_id")]
    public Guid TurnId { get; set; }

    [Column("invocation_id")]
    public Guid InvocationId { get; set; }

    [Column("sequence")]
    public int Sequence { get; set; }

    [Column("tool_name")]
    public string ToolName { get; set; } = string.Empty;

    [Column("arguments_json")]
    public string ArgumentsJson { get; set; } = string.Empty;

    [Column("result_json")]
    public string? ResultJson { get; set; }

    [Column("status")]
    public string Status { get; set; } = AssistantInvocationStatus.Started;

    [Column("error_category")]
    public string? ErrorCategory { get; set; }

    [Column("started_at")]
    public DateTimeOffset StartedAt { get; set; }

    [Column("completed_at")]
    public DateTimeOffset? CompletedAt { get; set; }

    [Column("duration_ms")]
    public double? DurationMs { get; set; }

    [Column("proposed_change_count")]
    public int ProposedChangeCount { get; set; }
}

[Table("assistant_feedback")]
public sealed class AssistantFeedback
{
    [Column("id")]
    public Guid Id { get; set; }

    [Column("turn_id")]
    public Guid TurnId { get; set; }

    [Column("rating")]
    public string Rating { get; set; } = string.Empty;

    [Column("comment")]
    public string? Comment { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<AssistantFeedbackReason> Reasons { get; set; } = [];
}

public static class AssistantFeedbackRating
{
    public const string Up = "up";
    public const string Down = "down";
}

[Table("assistant_feedback_reasons")]
public sealed class AssistantFeedbackReason
{
    [Column("feedback_id")]
    public Guid FeedbackId { get; set; }

    [Column("reason_code")]
    public string ReasonCode { get; set; } = string.Empty;

    public AssistantFeedback Feedback { get; set; } = null!;
}

[Table("assistant_telemetry_deletion_requests")]
public sealed class AssistantTelemetryDeletionRequest
{
    [Column("id")]
    public Guid Id { get; set; }

    [Column("table_name")]
    public string TableName { get; set; } = string.Empty;

    [Column("dimension_name")]
    public string DimensionName { get; set; } = string.Empty;

    [Column("dimension_value")]
    public string DimensionValue { get; set; } = string.Empty;

    [Column("status")]
    public string Status { get; set; } = AssistantTelemetryDeletionStatus.Pending;

    [Column("attempt_count")]
    public int AttemptCount { get; set; }

    [Column("next_attempt_at")]
    public DateTimeOffset NextAttemptAt { get; set; }

    [Column("lease_id")]
    public Guid? LeaseId { get; set; }

    [Column("azure_operation_id")]
    public string? AzureOperationId { get; set; }

    [Column("last_error")]
    public string? LastError { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("completed_at")]
    public DateTimeOffset? CompletedAt { get; set; }
}

public static class AssistantTelemetryDeletionStatus
{
    public const string Pending = "pending";
    public const string Submitting = "submitting";
    public const string Submitted = "submitted";
    public const string Completed = "completed";
    public const string Failed = "failed";
}
