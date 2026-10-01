using Glosify.Services.Ai.Assistant.Runtime;

namespace Glosify.Services.Ai.Assistant;

/// <summary>
/// Controller-facing assistant façade. Every message is executed by the durable run runtime;
/// the request-reply methods here start a run and wait for it, so the routes and DTOs of the
/// web panel and the mobile API stay as they were.
/// </summary>
internal sealed class AssistantOrchestrator(
    AssistantThreadStore threads,
    AssistantSyncAdapter runs,
    AssistantChangeWorkflow changes,
    AssistantFeedbackService feedback) : IAssistantOrchestrator
{
    public Task<IReadOnlyList<AssistantChatSummary>> ListChatsAsync(string userId, CancellationToken cancellationToken = default) =>
        threads.ListAsync(userId, cancellationToken);

    public Task<AssistantChatSummary> CreateChatAsync(string userId, Guid? contextQuizId = null, CancellationToken cancellationToken = default, Guid? contextTranscriptId = null, Guid? contextBookDocumentId = null) =>
        threads.CreateAsync(userId, contextQuizId, contextTranscriptId, contextBookDocumentId, cancellationToken);

    public Task<AssistantChatSummary> UpdateChatAsync(Guid threadId, string userId, string? title = null, Guid? contextQuizId = null, bool updateContext = false, CancellationToken cancellationToken = default, Guid? contextTranscriptId = null, Guid? contextBookDocumentId = null) =>
        threads.UpdateAsync(threadId, userId, title, contextQuizId, updateContext, contextTranscriptId, contextBookDocumentId, cancellationToken);

    public Task DeleteChatAsync(Guid threadId, string userId, CancellationToken cancellationToken = default) =>
        threads.DeleteAsync(threadId, userId, cancellationToken);

    public Task<AssistantHistory> GetChatHistoryAsync(Guid threadId, string userId, CancellationToken cancellationToken = default) =>
        threads.GetChatHistoryAsync(threadId, userId, cancellationToken);

    public async Task<AssistantTurnResponse> SendChatMessageAsync(Guid threadId, string userId, string userMessage, Guid? contextQuizId = null, string? focusedWordId = null, AssistantDocumentContext? documentContext = null, CancellationToken cancellationToken = default, Guid? transcriptId = null, Guid? bookDocumentId = null, AssistantTranscriptPageContext? transcriptPageContext = null)
    {
        var thread = await threads.GetOwnedAsync(threadId, userId, cancellationToken);
        return await runs.SendAsync(thread.Id, userId, new AssistantRunInput(
            userMessage,
            contextQuizId,
            focusedWordId,
            documentContext,
            transcriptId ?? thread.ContextTranscriptId,
            bookDocumentId ?? thread.ContextBookDocumentId,
            transcriptPageContext), cancellationToken);
    }

    public async Task<AssistantTurnResponse> SendMessageAsync(Guid quizId, string userId, string userMessage, string? focusedWordId = null, AssistantDocumentContext? documentContext = null, CancellationToken cancellationToken = default)
    {
        var thread = await threads.GetOrCreateDefaultAsync(userId, quizId, cancellationToken);
        return await runs.SendAsync(thread.Id, userId, new AssistantRunInput(
            userMessage,
            quizId,
            focusedWordId,
            documentContext,
            thread.ContextTranscriptId,
            thread.ContextBookDocumentId), cancellationToken);
    }

    public async Task<AssistantTurnResponse> SendGlobalMessageAsync(string userId, string userMessage, AssistantDocumentContext? documentContext = null, CancellationToken cancellationToken = default)
    {
        var thread = await threads.GetOrCreateDefaultAsync(userId, null, cancellationToken);
        return await runs.SendAsync(thread.Id, userId, new AssistantRunInput(
            userMessage,
            thread.ContextQuizId,
            DocumentContext: documentContext,
            TranscriptId: thread.ContextTranscriptId,
            BookDocumentId: thread.ContextBookDocumentId), cancellationToken);
    }

    public Task<AssistantHistory> GetHistoryAsync(Guid quizId, string userId, CancellationToken cancellationToken = default) =>
        threads.GetQuizHistoryAsync(quizId, userId, cancellationToken);

    public Task<AssistantHistory> GetGlobalHistoryAsync(string userId, CancellationToken cancellationToken = default) =>
        threads.GetGlobalHistoryAsync(userId, cancellationToken);

    public Task<AssistantApplyResult> ApplyPendingChangesAsync(Guid messageId, string userId, CancellationToken cancellationToken = default) =>
        changes.ApplyAsync(messageId, userId, cancellationToken);

    public Task<AssistantApplyResult> ApplyGlobalPendingChangesAsync(Guid messageId, string userId, CancellationToken cancellationToken = default) =>
        changes.ApplyAsync(messageId, userId, cancellationToken);

    public Task RejectPendingChangesAsync(Guid messageId, string userId, CancellationToken cancellationToken = default) =>
        changes.RejectAsync(messageId, userId, cancellationToken);

    public Task RejectGlobalPendingChangesAsync(Guid messageId, string userId, CancellationToken cancellationToken = default) =>
        changes.RejectAsync(messageId, userId, cancellationToken);

    public Task ResetGlobalSessionAsync(string userId, CancellationToken cancellationToken = default) =>
        changes.ResetAsync(userId, cancellationToken);

    public Task<AssistantFeedbackView> SaveFeedbackAsync(Guid turnId, string userId, string rating, IReadOnlyCollection<string>? reasonCodes, string? comment, CancellationToken cancellationToken = default) =>
        feedback.UpsertAsync(turnId, userId, rating, reasonCodes, comment, cancellationToken);

    public Task DeleteFeedbackAsync(Guid turnId, string userId, CancellationToken cancellationToken = default) =>
        feedback.DeleteAsync(turnId, userId, cancellationToken);

    public Task RecordClientDurationAsync(Guid turnId, string userId, double clientDurationMs, CancellationToken cancellationToken = default) =>
        feedback.RecordClientDurationAsync(turnId, userId, clientDurationMs, cancellationToken);
}
