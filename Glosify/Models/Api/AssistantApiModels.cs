namespace Glosify.Models.Api;

public sealed record AssistantChatInput(
    Guid? ContextQuizId,
    Guid? ContextTranscriptId = null,
    Guid? ContextBookDocumentId = null);

public sealed record AssistantSendInput(
    string Message,
    Guid? ContextQuizId,
    string? FocusedWordId,
    string? Model,
    Guid? DocumentId,
    int? PageNumber,
    Guid? CustomQuizId,
    Guid? TranscriptId = null,
    Guid? BookDocumentId = null);
