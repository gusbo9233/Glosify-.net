namespace Glosify.Services;

public interface IAssistantOrchestrator
{
    Task<AssistantTurnResponse> SendMessageAsync(
        Guid quizId,
        string userId,
        string userMessage,
        string? focusedWordId = null,
        CancellationToken cancellationToken = default);

    Task<AssistantHistory> GetHistoryAsync(
        Guid quizId,
        string userId,
        CancellationToken cancellationToken = default);

    Task<int> ApplyPendingChangesAsync(
        Guid messageId,
        string userId,
        CancellationToken cancellationToken = default);

    Task RejectPendingChangesAsync(
        Guid messageId,
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

public sealed record AssistantMessageView(
    Guid Id,
    string Role,
    string Text,
    IReadOnlyList<AssistantToolEvent> ToolEvents,
    IReadOnlyList<AssistantPendingChangeView> PendingChanges,
    string Status,
    DateTimeOffset CreatedAt);
