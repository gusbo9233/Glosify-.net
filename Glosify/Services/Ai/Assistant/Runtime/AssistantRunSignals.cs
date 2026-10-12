using System.Diagnostics.Metrics;

namespace Glosify.Services.Ai.Assistant.Runtime;

/// <summary>
/// Wakes whoever is watching a run as soon as it changes, so live updates arrive without
/// tight polling. In-process only, which matches the single-instance deployment; watchers
/// also time out and re-read the database, so a missed signal only delays an update.
/// </summary>
internal sealed class AssistantRunSignals
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Waiter> _waiters = [];
    internal int WaitingRunCount { get { lock (_gate) return _waiters.Count; } }

    private sealed class Waiter
    {
        public TaskCompletionSource Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Readers { get; set; }
    }

    public void Notify(Guid runId)
    {
        Waiter? waiter;
        lock (_gate) _waiters.Remove(runId, out waiter);
        waiter?.Signal.TrySetResult();
    }

    public async Task WaitAsync(Guid runId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Waiter waiter;
        lock (_gate)
        {
            if (!_waiters.TryGetValue(runId, out waiter!)) _waiters[runId] = waiter = new Waiter();
            waiter.Readers++;
        }
        try
        {
            await waiter.Signal.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
        }
        finally
        {
            lock (_gate)
            {
                if (--waiter.Readers == 0 && _waiters.TryGetValue(runId, out var current) && ReferenceEquals(current, waiter))
                    _waiters.Remove(runId);
            }
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
