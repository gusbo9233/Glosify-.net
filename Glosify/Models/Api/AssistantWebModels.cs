namespace Glosify.Models.Api;

public sealed class SendMessageInput
{
    public string Message { get; set; } = string.Empty;
    public Guid? ContextQuizId { get; set; }
    public string? FocusedWordId { get; set; }
    public string? Model { get; set; }
    public DocumentContextInput? DocumentContext { get; set; }
    public Guid? CustomQuizId { get; set; }
}

public sealed class ChatMutationInput
{
    public string? Title { get; set; }
    public Guid? ContextQuizId { get; set; }
    public bool UpdateContext { get; set; }
}

public sealed class DocumentContextInput
{
    public Guid DocumentId { get; set; }
    public int PageNumber { get; set; }
}
