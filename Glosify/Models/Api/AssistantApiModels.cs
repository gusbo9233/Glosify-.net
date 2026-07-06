namespace Glosify.Models.Api;

public sealed record AssistantChatInput(Guid? ContextQuizId);

public sealed record AssistantSendInput(
    string Message,
    Guid? ContextQuizId,
    string? FocusedWordId,
    string? Model,
    Guid? DocumentId,
    int? PageNumber);
