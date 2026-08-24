using System.ComponentModel.DataAnnotations;

namespace Glosify.Models.Api;

public sealed class SendMessageInput
{
    [Required]
    [StringLength(8000)]
    public string Message { get; set; } = string.Empty;
    public Guid? ContextQuizId { get; set; }
    public string? FocusedWordId { get; set; }
    public DocumentContextInput? DocumentContext { get; set; }
    public Guid? CustomQuizId { get; set; }
    public Guid? TranscriptId { get; set; }
    public Guid? BookDocumentId { get; set; }
    public TranscriptContextInput? TranscriptContext { get; set; }
}

public sealed class ChatMutationInput
{
    [StringLength(160)]
    public string? Title { get; set; }
    public Guid? ContextQuizId { get; set; }
    public bool UpdateContext { get; set; }
    public Guid? ContextTranscriptId { get; set; }
    public Guid? ContextBookDocumentId { get; set; }
}

/// <summary>
/// The transcript page the reader is showing, if the assistant was opened from it. Unlike
/// <see cref="DocumentContextInput"/> this carries no id: the transcript is already named
/// by TranscriptId, and the client only sends a page for the transcript it is displaying.
/// </summary>
public sealed class TranscriptContextInput
{
    [Range(1, int.MaxValue)]
    public int Page { get; set; }

    [StringLength(20)]
    public string? Stream { get; set; }
}

public sealed class DocumentContextInput
{
    public Guid DocumentId { get; set; }

    [Range(1, int.MaxValue)]
    public int PageNumber { get; set; }
}
