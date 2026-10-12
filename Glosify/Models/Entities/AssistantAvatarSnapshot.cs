using System.ComponentModel.DataAnnotations.Schema;

namespace Glosify.Models.Entities;

/// <summary>An immutable version shared by requests in one assistant chat.</summary>
[Table("assistant_avatar_snapshots")]
public sealed class AssistantAvatarSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ThreadId { get; set; }
    public Guid ConversationId { get; set; }
    public string Language { get; set; } = string.Empty;
    public int TurnCount { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public string ContentJson { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
