using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant.Runtime;

/// <summary>
/// Serves the request-reply endpoints (the mobile API and older clients) from the durable
/// runtime: it starts a run in <see cref="AssistantRunModes.Sync"/> mode and waits for it.
/// </summary>
/// <remarks>
/// Only the wait is tied to the HTTP request. If the connection drops, the run still
/// finishes and its reply appears in the chat history. Sync runs never stop to ask: changes
/// that need approval come back as a proposal for the classic Apply button, and a question
/// becomes the reply.
/// </remarks>
internal sealed class AssistantSyncAdapter(
    GlosifyContext db,
    AssistantRunStore runs,
    IAssistantRunWaiter waiter,
    AssistantMessagePresenter presenter,
    AssistantThreadStore threads)
{
    public async Task<AssistantTurnResponse> SendAsync(
        Guid threadId,
        string userId,
        AssistantRunInput input,
        CancellationToken cancellationToken)
    {
        AssistantRunView view;
        try
        {
            view = await runs.StartAsync(threadId, userId, new AssistantRunStartInput(Guid.NewGuid().ToString("N"), input), cancellationToken, AssistantRunModes.Sync);
        }
        catch (AssistantRunConflictException)
        {
            throw new AssistantTurnInProgressException();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException)
        {
            // The request-reply endpoints report an unusable chat as a bad request.
            throw new InvalidOperationException(ex.Message, ex);
        }

        while (!AssistantRunStatus.IsTerminal(view.Status))
        {
            // Cancelling this token ends only the wait, never the run.
            await waiter.WaitAsync(view.Id, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            view = await runs.ViewAsync(view.Id, userId, cancellationToken);
        }

        return await ResponseAsync(view, userId, cancellationToken);
    }

    private async Task<AssistantTurnResponse> ResponseAsync(AssistantRunView view, string userId, CancellationToken cancellationToken)
    {
        var message = await db.AssistantMessages.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == view.MessageId, cancellationToken);
        var changes = presenter.ParseStoredChanges(message.PendingChangesJson);
        var labels = await threads.LoadWordLabelsAsync(message.ContextQuizId, changes, cancellationToken);
        var tools = await db.AssistantParts.AsNoTracking()
            .Where(part => part.RunId == view.Id && part.Type == AssistantPartTypes.Tool)
            .OrderBy(part => part.Step)
            .ThenBy(part => part.Sequence)
            .Select(part => new AssistantToolEvent(part.ToolName ?? "tool", part.InputJson ?? "{}", part.Title ?? string.Empty))
            .ToListAsync(cancellationToken);
        return new AssistantTurnResponse(
            view.ThreadId,
            view.TurnId,
            message.Id,
            presenter.ExtractVisibleText(message),
            tools,
            changes.Select(change => presenter.PresentPendingChange(change, labels)).ToList(),
            message.Status);
    }
}

/// <summary>How a request-reply caller waits for its run to move.</summary>
internal interface IAssistantRunWaiter
{
    Task WaitAsync(Guid runId, CancellationToken cancellationToken);
}

/// <summary>Waits for the worker's next checkpoint signal, re-reading at least every two seconds.</summary>
internal sealed class SignalRunWaiter(AssistantRunSignals signals) : IAssistantRunWaiter
{
    public Task WaitAsync(Guid runId, CancellationToken cancellationToken) =>
        signals.WaitAsync(runId, TimeSpan.FromSeconds(2), cancellationToken);
}
