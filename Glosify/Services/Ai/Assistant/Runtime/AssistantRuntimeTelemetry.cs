using System.Diagnostics.Metrics;

namespace Glosify.Services.Ai.Assistant.Runtime;

internal static class AssistantRuntimeTelemetry
{
    internal static readonly Meter Meter = new("Glosify.Assistant.Runtime");
    internal static readonly Counter<long> ModelAttempts = Meter.CreateCounter<long>("assistant.runtime.model_attempts");
    internal static readonly Counter<long> SavedChanges = Meter.CreateCounter<long>("assistant.runtime.saved_changes");
    internal static readonly Counter<long> Retries = Meter.CreateCounter<long>("assistant.runtime.retries");
    internal static readonly Counter<long> Tasks = Meter.CreateCounter<long>("assistant.runtime.task_transitions");
    internal static readonly Counter<long> Evaluations = Meter.CreateCounter<long>("assistant.runtime.evaluations");
}
