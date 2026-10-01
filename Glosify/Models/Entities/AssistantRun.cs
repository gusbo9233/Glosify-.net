using System.ComponentModel.DataAnnotations.Schema;

namespace Glosify.Models.Entities;

/// <summary>
/// One assistant request, executed durably by the background worker.
/// </summary>
/// <remarks>
/// A run owns no conversation state of its own: the messages and parts it writes are the
/// conversation, and the provider history is derived from them each step. The run only keeps
/// the bookkeeping that makes execution resumable — lease, revision, budget, and a small
/// loop state. Timestamps are UTC <see cref="DateTime"/> values because leases are compared
/// in SQL, which SQLite cannot do for <see cref="DateTimeOffset"/>.
/// </remarks>
[Table("assistant_runs")]
public sealed class AssistantRun
{
    [Column("id")]
    public Guid Id { get; set; }

    [Column("thread_id")]
    public Guid ThreadId { get; set; }

    [Column("user_id")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>Set until the run ends. A filtered unique index allows one active run per user.</summary>
    [Column("active_user_id")]
    public string? ActiveUserId { get; set; }

    [Column("idempotency_key")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [Column("request_hash")]
    public string RequestHash { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="AssistantRunModes.Interactive"/> for clients that can approve, answer, and
    /// resume; <see cref="AssistantRunModes.Sync"/> for callers that only wait for a reply.
    /// </summary>
    [Column("mode")]
    public string Mode { get; set; } = AssistantRunModes.Interactive;

    [Column("status")]
    public string Status { get; set; } = AssistantRunStatus.Queued;

    [Column("reason")]
    public string? Reason { get; set; }

    [Column("request_json")]
    public string RequestJson { get; set; } = "{}";

    [Column("state_json")]
    public string StateJson { get; set; } = "{}";

    /// <summary>The model-maintained checklist shown to the user while work is in progress.</summary>
    [Column("plan_json")]
    public string? PlanJson { get; set; }

    /// <summary>The analytics turn, which also keys feedback on the reply.</summary>
    [Column("turn_id")]
    public Guid TurnId { get; set; }

    [Column("user_message_id")]
    public Guid UserMessageId { get; set; }

    /// <summary>The assistant message new parts are appended to. Steering starts a new one.</summary>
    [Column("current_message_id")]
    public Guid? CurrentMessageId { get; set; }

    [Column("revision")]
    public long Revision { get; set; }

    [Column("lease_id")]
    public Guid? LeaseId { get; set; }

    [Column("lease_until")]
    public DateTime LeaseUntil { get; set; }

    [Column("retry_at")]
    public DateTime? RetryAt { get; set; }

    [Column("window_started_at")]
    public DateTime WindowStartedAt { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }

    [Column("completed_at")]
    public DateTime? CompletedAt { get; set; }

    /// <summary>Model calls in the current execution window.</summary>
    [Column("steps")]
    public int Steps { get; set; }

    [Column("total_steps")]
    public int TotalSteps { get; set; }

    [Column("failures")]
    public int Failures { get; set; }

    [Column("input_tokens")]
    public long InputTokens { get; set; }

    [Column("cached_input_tokens")]
    public long CachedInputTokens { get; set; }

    [Column("output_tokens")]
    public long OutputTokens { get; set; }

    [Column("saved_changes")]
    public int SavedChanges { get; set; }

    [Column("undone_at")]
    public DateTime? UndoneAt { get; set; }
}

public static class AssistantRunModes
{
    public const string Interactive = "interactive";
    public const string Sync = "sync";
}

public static class AssistantRunStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string RetryWait = "retry_wait";
    public const string AwaitingApproval = "awaiting_approval";
    public const string AwaitingInput = "awaiting_input";
    public const string Paused = "paused";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";

    public static bool IsRunnable(string status) => status is Queued or Running or RetryWait;

    public static bool IsTerminal(string status) => status is Completed or Cancelled or Failed;

    public static bool IsWaiting(string status) => status is AwaitingApproval or AwaitingInput or Paused;
}

/// <summary>
/// One piece of a message: visible text, a tool call and its result, or synthetic context
/// the model sees but the user does not.
/// </summary>
/// <remarks>
/// A tool part carries the whole call — input, state, the output the model receives, and the
/// title and metadata the UI renders — so one row is the source for both. Provider history is
/// rebuilt from parts on every step rather than stored separately.
/// </remarks>
[Table("assistant_parts")]
public sealed class AssistantPart
{
    [Column("id")]
    public Guid Id { get; set; }

    [Column("message_id")]
    public Guid MessageId { get; set; }

    [Column("run_id")]
    public Guid? RunId { get; set; }

    [Column("sequence")]
    public int Sequence { get; set; }

    /// <summary>The model call that produced this part, counted across the whole run.</summary>
    [Column("step")]
    public int Step { get; set; }

    [Column("type")]
    public string Type { get; set; } = AssistantPartTypes.Text;

    [Column("text")]
    public string? Text { get; set; }

    [Column("tool_name")]
    public string? ToolName { get; set; }

    [Column("call_id")]
    public string? CallId { get; set; }

    [Column("input_json")]
    public string? InputJson { get; set; }

    [Column("state")]
    public string? State { get; set; }

    /// <summary>What the model receives as the tool result.</summary>
    [Column("output")]
    public string? Output { get; set; }

    /// <summary>A one-line description of the call for the activity list.</summary>
    [Column("title")]
    public string? Title { get; set; }

    /// <summary>UI detail: proposed changes awaiting approval, a question and its options, counts.</summary>
    [Column("metadata_json")]
    public string? MetadataJson { get; set; }

    /// <summary>
    /// Raw provider output items of one model call, replayed verbatim within the run so
    /// encrypted reasoning stays paired with its function calls.
    /// </summary>
    [Column("provider_json")]
    public string? ProviderJson { get; set; }

    /// <summary>Set when context management cleared this part's output from later requests.</summary>
    [Column("compacted_at")]
    public DateTime? CompactedAt { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("completed_at")]
    public DateTime? CompletedAt { get; set; }
}

public static class AssistantPartTypes
{
    /// <summary>Visible text from the user or the assistant.</summary>
    public const string Text = "text";

    /// <summary>A created quiz link: Text holds its id and Title its display name.</summary>
    public const string QuizLink = "quiz_link";

    /// <summary>A tool call and its result.</summary>
    public const string Tool = "tool";

    /// <summary>App facts attached to a user message: quiz, languages, the page being read.</summary>
    public const string Context = "context";

    /// <summary>A runtime note to the model, such as unfinished work or a declined change.</summary>
    public const string Reminder = "reminder";

    /// <summary>A compaction summary standing in for older conversation.</summary>
    public const string Summary = "summary";

    /// <summary>Provider output items for one model call.</summary>
    public const string Step = "step";
}

public static class AssistantToolStates
{
    public const string Pending = "pending";
    public const string Completed = "completed";
    public const string Error = "error";
    public const string AwaitingApproval = "awaiting_approval";
    public const string Approved = "approved";
    public const string AwaitingInput = "awaiting_input";
    public const string Rejected = "rejected";
    public const string Superseded = "superseded";

    public static bool IsSettled(string? state) => state is Completed or Error or Rejected or Superseded;
}

/// <summary>
/// One saved change, recorded with enough of its before and after state to undo it.
/// </summary>
[Table("assistant_changes")]
public sealed class AssistantChange
{
    [Column("id")]
    public Guid Id { get; set; }

    [Column("run_id")]
    public Guid RunId { get; set; }

    [Column("part_id")]
    public Guid PartId { get; set; }

    [Column("user_id")]
    public string UserId { get; set; } = string.Empty;

    [Column("sequence")]
    public int Sequence { get; set; }

    [Column("kind")]
    public string Kind { get; set; } = string.Empty;

    [Column("entity_type")]
    public string EntityType { get; set; } = string.Empty;

    [Column("entity_id")]
    public string EntityId { get; set; } = string.Empty;

    [Column("quiz_id")]
    public Guid? QuizId { get; set; }

    [Column("before_json")]
    public string? BeforeJson { get; set; }

    [Column("after_json")]
    public string? AfterJson { get; set; }

    [Column("status")]
    public string Status { get; set; } = AssistantChangeStatus.Applied;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}

public static class AssistantChangeStatus
{
    public const string Applied = "applied";
    public const string Undone = "undone";

    /// <summary>The user changed the item afterwards, so undoing it would discard their edit.</summary>
    public const string Kept = "kept";
}

/// <summary>An advisory review of one tool call, created only when evaluation is enabled.</summary>
[Table("assistant_tool_evaluations")]
public sealed class AssistantToolEvaluation
{
    [Column("id")]
    public Guid Id { get; set; }

    [Column("run_id")]
    public Guid RunId { get; set; }

    [Column("part_id")]
    public Guid PartId { get; set; }

    [Column("tool_name")]
    public string ToolName { get; set; } = string.Empty;

    [Column("status")]
    public string Status { get; set; } = "pending";

    [Column("snapshot_json")]
    public string SnapshotJson { get; set; } = "{}";

    [Column("snapshot_hash")]
    public string SnapshotHash { get; set; } = string.Empty;

    [Column("result_json")]
    public string? ResultJson { get; set; }

    [Column("evaluation_json")]
    public string? EvaluationJson { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
