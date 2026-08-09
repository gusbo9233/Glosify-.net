using System.ComponentModel.DataAnnotations;

namespace Glosify.Models.Api;

public sealed class SendMessageInput
{
    [Required]
    [StringLength(8000)]
    public string Message { get; set; } = string.Empty;
    public Guid? ContextQuizId { get; set; }
    public string? FocusedWordId { get; set; }
    public string? Model { get; set; }
    public DocumentContextInput? DocumentContext { get; set; }
    public Guid? CustomQuizId { get; set; }
    public Guid? TranscriptId { get; set; }
    public Guid? BookDocumentId { get; set; }
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

public sealed class DocumentContextInput
{
    public Guid DocumentId { get; set; }

    [Range(1, int.MaxValue)]
    public int PageNumber { get; set; }
}
