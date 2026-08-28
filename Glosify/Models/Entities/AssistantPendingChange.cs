using System.ComponentModel.DataAnnotations.Schema;

namespace Glosify.Models.Entities;

/// <summary>
/// A change proposed by a tool call and awaiting the user's Apply.
/// </summary>
/// <remarks>
/// While the assistant loop runs in-process these accumulate in memory and are written
/// onto the assistant message. Historical hosted-agent tool calls had no such turn
/// to collect it, so it lands here instead, grouped by the conversation that produced it.
/// </remarks>
[Table("assistant_pending_changes")]
public class AssistantPendingChange
{
    [Column("id")]
    public Guid Id { get; set; }

    /// <summary>Groups the changes a single assistant response produced.</summary>
    [Column("conversation_id")]
    public Guid ConversationId { get; set; }

    [Column("user_id")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>Set once the response is persisted, so Apply can find these by message.</summary>
    [Column("message_id")]
    public Guid? MessageId { get; set; }

    [Column("context_quiz_id")]
    public Guid? ContextQuizId { get; set; }

    /// <summary>Preserves the order tools were called in; Apply depends on it.</summary>
    [Column("sequence")]
    public int Sequence { get; set; }

    [Column("kind")]
    public string Kind { get; set; } = string.Empty;

    [Column("payload_json")]
    public string PayloadJson { get; set; } = string.Empty;

    [Column("status")]
    public string Status { get; set; } = AssistantPendingChangeStatus.Pending;

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }
}

public static class AssistantPendingChangeStatus
{
    public const string Pending = "pending";
    public const string Applied = "applied";
    public const string Rejected = "rejected";
}
