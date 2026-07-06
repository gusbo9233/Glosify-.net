namespace Glosify.Services.Ai.Assistant;

public interface IAssistantOrchestrator
{
    Task<IReadOnlyList<AssistantChatSummary>> ListChatsAsync(
        string userId,
        CancellationToken cancellationToken = default);

    Task<AssistantChatSummary> CreateChatAsync(
        string userId,
        Guid? contextQuizId = null,
        CancellationToken cancellationToken = default);

    Task<AssistantChatSummary> UpdateChatAsync(
        Guid threadId,
        string userId,
        string? title = null,
        Guid? contextQuizId = null,
        bool updateContext = false,
        CancellationToken cancellationToken = default);

    Task DeleteChatAsync(
        Guid threadId,
        string userId,
        CancellationToken cancellationToken = default);

    Task<AssistantHistory> GetChatHistoryAsync(
        Guid threadId,
        string userId,
        CancellationToken cancellationToken = default);

    Task<AssistantTurnResponse> SendChatMessageAsync(
        Guid threadId,
        string userId,
        string userMessage,
        Guid? contextQuizId = null,
        string? focusedWordId = null,
        string? model = null,
        AssistantDocumentContext? documentContext = null,
        CancellationToken cancellationToken = default);

    Task<AssistantTurnResponse> SendMessageAsync(
        Guid quizId,
        string userId,
        string userMessage,
        string? focusedWordId = null,
        string? model = null,
        AssistantDocumentContext? documentContext = null,
        CancellationToken cancellationToken = default);

    Task<AssistantTurnResponse> SendGlobalMessageAsync(
        string userId,
        string userMessage,
        string? model = null,
        AssistantDocumentContext? documentContext = null,
        CancellationToken cancellationToken = default);

    Task<AssistantHistory> GetHistoryAsync(
        Guid quizId,
        string userId,
        CancellationToken cancellationToken = default);

    Task<AssistantHistory> GetGlobalHistoryAsync(
        string userId,
        CancellationToken cancellationToken = default);

    Task<AssistantApplyResult> ApplyPendingChangesAsync(
        Guid messageId,
        string userId,
        CancellationToken cancellationToken = default);

    Task<AssistantApplyResult> ApplyGlobalPendingChangesAsync(
        Guid messageId,
        string userId,
        CancellationToken cancellationToken = default);

    Task RejectPendingChangesAsync(
        Guid messageId,
        string userId,
        CancellationToken cancellationToken = default);

    Task RejectGlobalPendingChangesAsync(
        Guid messageId,
        string userId,
        CancellationToken cancellationToken = default);

    Task ResetGlobalSessionAsync(
        string userId,
        CancellationToken cancellationToken = default);
}

public sealed record AssistantTurnResponse(
    Guid ThreadId,
    Guid AssistantMessageId,
    string AssistantText,
    IReadOnlyList<AssistantToolEvent> ToolEvents,
    IReadOnlyList<AssistantPendingChangeView> PendingChanges,
    string Status);

public sealed record AssistantToolEvent(string Name, string ArgsJson, string ResultSummary);

public sealed record AssistantPendingChangeView(string Kind, string Summary, string PayloadJson);

public sealed record AssistantHistory(
    Guid? ThreadId,
    IReadOnlyList<AssistantMessageView> Messages);

public sealed record AssistantChatSummary(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Preview,
    Guid? ContextQuizId,
    string? ContextQuizName);

public sealed record AssistantMessageView(
    Guid Id,
    string Role,
    string Text,
    IReadOnlyList<AssistantToolEvent> ToolEvents,
    IReadOnlyList<AssistantPendingChangeView> PendingChanges,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record AssistantDocumentContext(Guid DocumentId, int PageNumber);
