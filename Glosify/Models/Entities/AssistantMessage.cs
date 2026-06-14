using System.ComponentModel.DataAnnotations.Schema;

namespace Glosify.Models.Entities;

[Table("assistant_messages")]
public class AssistantMessage
{
    [Column("id")]
    public Guid Id { get; set; }

    [Column("thread_id")]
    public Guid ThreadId { get; set; }

    [Column("context_quiz_id")]
    public Guid? ContextQuizId { get; set; }

    [Column("sequence")]
    public int Sequence { get; set; }

    [Column("role")]
    public string Role { get; set; } = string.Empty;

    [Column("content_json")]
    public string ContentJson { get; set; } = string.Empty;

    [Column("pending_changes_json")]
    public string? PendingChangesJson { get; set; }

    [Column("status")]
    public string Status { get; set; } = AssistantMessageStatus.Active;

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }
}

public static class AssistantMessageRole
{
    public const string User = "user";
    public const string Model = "model";
}

public static class AssistantMessageStatus
{
    public const string Active = "active";
    public const string Applied = "applied";
    public const string Rejected = "rejected";
}
