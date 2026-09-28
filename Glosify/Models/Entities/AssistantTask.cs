namespace Glosify.Models.Entities;

/// <summary>Operational journal, deliberately independent of analytics capture.</summary>
public sealed class AssistantTask
{
    public Guid Id { get; set; }
    public Guid ThreadId { get; set; }
    public string UserId { get; set; } = "";
    public string? ActiveUserId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string Status { get; set; } = "queued";
    public string? Reason { get; set; }
    public string RequestJson { get; set; } = "{}";
    public string StateJson { get; set; } = "{}";
    public string SteeringJson { get; set; } = "[]";
    public long Revision { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTime LeaseUntil { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime WindowStartedAt { get; set; }
    public DateTime? RetryAt { get; set; }
    public int ModelCalls { get; set; }
    public int Tokens { get; set; }
    public int Failures { get; set; }
    public int SavedChanges { get; set; }
    public bool ManualApproval { get; set; }
    public bool ApprovalGranted { get; set; }
    public string? ResultJson { get; set; }
}

public sealed class AssistantTaskCall
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public int Sequence { get; set; }
    public string ToolName { get; set; } = "";
    public string ArgumentsJson { get; set; } = "{}";
    public string Status { get; set; } = "pending";
    public string? ResultJson { get; set; }
    public string SnapshotJson { get; set; } = "{}";
    public string SnapshotHash { get; set; } = "";
    public string EvaluationStatus { get; set; } = "pending";
    public string? EvaluationJson { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class AssistantTaskAttempt
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public string Status { get; set; } = "started";
    public string RequestJson { get; set; } = "{}";
    public string? ResponseJson { get; set; }
    public string? ErrorCategory { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
