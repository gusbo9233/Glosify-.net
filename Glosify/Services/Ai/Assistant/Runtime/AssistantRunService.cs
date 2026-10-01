using System.Runtime.CompilerServices;
using Glosify.Models.Entities;

namespace Glosify.Services.Ai.Assistant.Runtime;

/// <summary>Controller-facing access to runs: start, watch, command, and undo.</summary>
public interface IAssistantRunService
{
    Task<AssistantRunView> StartAsync(Guid threadId, string userId, AssistantRunStartInput input, CancellationToken cancellationToken);

    Task<AssistantRunView?> LatestAsync(Guid threadId, string userId, CancellationToken cancellationToken);

    Task<AssistantRunView> ViewAsync(Guid id, string userId, CancellationToken cancellationToken);

    Task<AssistantRunView> CommandAsync(Guid id, string userId, string command, AssistantRunCommand input, CancellationToken cancellationToken);

    Task<AssistantUndoResult> UndoAsync(Guid id, string userId, CancellationToken cancellationToken);

    /// <summary>
    /// The run's view each time its revision moves past <paramref name="seenRevision"/>, until it
    /// ends or <paramref name="lifetime"/> passes. The first view is read before streaming starts,
    /// so an unknown run fails immediately.
    /// </summary>
    Task<IAsyncEnumerable<AssistantRunView>> WatchAsync(Guid id, string userId, long? seenRevision, TimeSpan lifetime, CancellationToken cancellationToken);
}

internal sealed class AssistantRunService(
    AssistantRunStore store,
    AssistantUndoService undo,
    AssistantRunSignals signals,
    TimeProvider clock) : IAssistantRunService
{
    public Task<AssistantRunView> StartAsync(Guid threadId, string userId, AssistantRunStartInput input, CancellationToken cancellationToken) =>
        store.StartAsync(threadId, userId, input, cancellationToken);

    public Task<AssistantRunView?> LatestAsync(Guid threadId, string userId, CancellationToken cancellationToken) =>
        store.LatestAsync(threadId, userId, cancellationToken);

    public Task<AssistantRunView> ViewAsync(Guid id, string userId, CancellationToken cancellationToken) =>
        store.ViewAsync(id, userId, cancellationToken);

    public Task<AssistantRunView> CommandAsync(Guid id, string userId, string command, AssistantRunCommand input, CancellationToken cancellationToken) =>
        store.CommandAsync(id, userId, command, input, cancellationToken);

    public Task<AssistantUndoResult> UndoAsync(Guid id, string userId, CancellationToken cancellationToken) =>
        undo.UndoAsync(id, userId, cancellationToken);

    public async Task<IAsyncEnumerable<AssistantRunView>> WatchAsync(
        Guid id,
        string userId,
        long? seenRevision,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        var first = await store.ViewAsync(id, userId, cancellationToken);
        return Watch(first, userId, seenRevision, clock.GetUtcNow() + lifetime, cancellationToken);
    }

    private async IAsyncEnumerable<AssistantRunView> Watch(
        AssistantRunView view,
        string userId,
        long? seen,
        DateTimeOffset deadline,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (view.Revision != seen)
            {
                seen = view.Revision;
                yield return view;
            }

            if (AssistantRunStatus.IsTerminal(view.Status) || clock.GetUtcNow() >= deadline)
            {
                yield break;
            }

            await signals.WaitAsync(view.Id, TimeSpan.FromSeconds(5), cancellationToken);
            view = await store.ViewAsync(view.Id, userId, cancellationToken);
        }
    }
}
