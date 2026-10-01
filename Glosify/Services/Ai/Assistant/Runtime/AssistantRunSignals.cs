using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Glosify.Services.Ai.Assistant.Runtime;

/// <summary>
/// Wakes whoever is watching a run as soon as it changes, so live updates arrive without
/// tight polling. In-process only, which matches the single-instance deployment; watchers
/// also time out and re-read the database, so a missed signal only delays an update.
/// </summary>
internal sealed class AssistantRunSignals
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _waiters = new();

    public void Notify(Guid runId)
    {
        if (_waiters.TryRemove(runId, out var waiter))
        {
            waiter.TrySetResult();
        }
    }

    public async Task WaitAsync(Guid runId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var waiter = _waiters.GetOrAdd(runId, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            await waiter.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
        }
    }
}

internal static class AssistantRunTelemetry
{
    internal const string MeterName = "Glosify.Assistant.Runtime";
    internal static readonly Meter Meter = new(MeterName);
    internal static readonly Counter<long> ModelCalls = Meter.CreateCounter<long>("assistant.runtime.model_calls");
    internal static readonly Counter<long> SavedChanges = Meter.CreateCounter<long>("assistant.runtime.saved_changes");
    internal static readonly Counter<long> Retries = Meter.CreateCounter<long>("assistant.runtime.retries");
    internal static readonly Counter<long> Transitions = Meter.CreateCounter<long>("assistant.runtime.transitions");
    internal static readonly Counter<long> Runs = Meter.CreateCounter<long>("assistant.runtime.runs");
    internal static readonly Counter<long> Undos = Meter.CreateCounter<long>("assistant.runtime.undos");
    internal static readonly Counter<long> Evaluations = Meter.CreateCounter<long>("assistant.runtime.evaluations");
}
