namespace Glosify.Services.Ai.Assistant;

public interface IAssistantOrchestrator
{
    Task<IReadOnlyList<AssistantChatSummary>> ListChatsAsync(
        string userId,
        CancellationToken cancellationToken = default);

    Task<AssistantChatSummary> CreateChatAsync(
        string userId,
        Guid? contextQuizId = null,
        CancellationToken cancellationToken = default,
        Guid? contextTranscriptId = null,
        Guid? contextBookDocumentId = null);

    Task<AssistantChatSummary> UpdateChatAsync(
        Guid threadId,
        string userId,
        string? title = null,
        Guid? contextQuizId = null,
        bool updateContext = false,
        CancellationToken cancellationToken = default,
        Guid? contextTranscriptId = null,
        Guid? contextBookDocumentId = null);

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
        Guid? customQuizId = null,
        CancellationToken cancellationToken = default,
        Guid? transcriptId = null,
        Guid? bookDocumentId = null,
        AssistantTranscriptPageContext? transcriptPageContext = null);

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

    Task<AssistantFeedbackView> SaveFeedbackAsync(
        Guid turnId,
        string userId,
        string rating,
        IReadOnlyCollection<string>? reasonCodes,
        string? comment,
        CancellationToken cancellationToken = default);

    Task DeleteFeedbackAsync(
        Guid turnId,
        string userId,
        CancellationToken cancellationToken = default);

    Task RecordClientDurationAsync(
        Guid turnId,
        string userId,
        double clientDurationMs,
        CancellationToken cancellationToken = default);
}

public sealed record AssistantTurnResponse(
    Guid ThreadId,
    Guid TurnId,
    Guid AssistantMessageId,
    string AssistantText,
    IReadOnlyList<AssistantToolEvent> ToolEvents,
    IReadOnlyList<AssistantPendingChangeView> PendingChanges,
    string Status,
    AssistantFeedbackView? Feedback = null);

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
    string? ContextQuizName,
    Guid? ContextTranscriptId,
    string? ContextTranscriptTitle,
    Guid? ContextBookDocumentId,
    string? ContextBookTitle);

public sealed record AssistantMessageView(
    Guid Id,
    Guid? TurnId,
    string Role,
    string Text,
    IReadOnlyList<AssistantToolEvent> ToolEvents,
    IReadOnlyList<AssistantPendingChangeView> PendingChanges,
    string Status,
    DateTimeOffset CreatedAt,
    bool CanRate,
    AssistantFeedbackView? Feedback = null);

public sealed record AssistantFeedbackView(
    string Rating,
    IReadOnlyList<string> ReasonCodes,
    string? Comment,
    DateTimeOffset UpdatedAt);

public sealed record AssistantDocumentContext(Guid DocumentId, int PageNumber);

/// <summary>
/// Which page of a saved transcript the user is looking at while they type. Unlike a book
/// page this is not inlined into the prompt — a transcript page is up to 100 captions —
/// but naming it is what lets "this page" and "the first page" mean the same thing to the
/// user and to the model, which reads the very same pages through get_saved_transcript.
/// Null whenever the assistant is open somewhere other than the transcript reader.
/// </summary>
public sealed record AssistantTranscriptPageContext(int Page, string? Stream);
