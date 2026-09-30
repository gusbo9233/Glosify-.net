using Glosify.Services.Ai.Assistant.Runtime;

namespace Glosify.Services.Ai.Assistant;

/// <summary>
/// Controller-facing assistant façade. Persistence, turn execution, and change-review
/// workflows are separate scoped collaborators; routes and public DTOs remain stable.
/// </summary>
internal sealed class AssistantOrchestrator(
    AssistantThreadStore threads,
    AssistantTurnRunner turns,
    AssistantChangeWorkflow changes,
    AssistantFeedbackService feedback,
    AssistantDurableAdapter? durable = null) : IAssistantOrchestrator
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

    public Task<AssistantTurnResponse> SendChatMessageAsync(Guid threadId, string userId, string userMessage, Guid? contextQuizId = null, string? focusedWordId = null, AssistantDocumentContext? documentContext = null, CancellationToken cancellationToken = default, Guid? transcriptId = null, Guid? bookDocumentId = null, AssistantTranscriptPageContext? transcriptPageContext = null) =>
        durable?.Enabled == true
            ? durable.SendAsync(threadId, userId, new(userMessage, contextQuizId, focusedWordId, documentContext, transcriptId, bookDocumentId, transcriptPageContext), cancellationToken)
            : turns.RunChatAsync(threadId, userId, userMessage, contextQuizId, focusedWordId, documentContext, transcriptId, bookDocumentId, transcriptPageContext, cancellationToken);

    public async Task<AssistantTurnResponse> SendMessageAsync(Guid quizId, string userId, string userMessage, string? focusedWordId = null, AssistantDocumentContext? documentContext = null, CancellationToken cancellationToken = default)
    {
        if (durable?.Enabled != true) return await turns.RunQuizAsync(quizId, userId, userMessage, focusedWordId, documentContext, cancellationToken);
        var thread = await threads.GetOrCreateDefaultAsync(userId, quizId, cancellationToken);
        return await durable.SendAsync(thread.Id, userId, new(userMessage, quizId, focusedWordId, documentContext), cancellationToken);
    }

    public async Task<AssistantTurnResponse> SendGlobalMessageAsync(string userId, string userMessage, AssistantDocumentContext? documentContext = null, CancellationToken cancellationToken = default)
    {
        if (durable?.Enabled != true) return await turns.RunGlobalAsync(userId, userMessage, documentContext, cancellationToken);
        var thread = await threads.GetOrCreateDefaultAsync(userId, null, cancellationToken);
        return await durable.SendAsync(thread.Id, userId, new(userMessage, thread.ContextQuizId, DocumentContext: documentContext), cancellationToken);
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
