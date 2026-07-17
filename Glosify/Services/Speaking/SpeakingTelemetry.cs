using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Glosify.Services.Speaking;

public static class SpeakingTelemetry
{
    public const string ActivitySourceName = "Glosify.Speaking";
    public const string MeterName = "Glosify.Speaking";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);
    public static readonly Counter<long> SessionsCreated = Meter.CreateCounter<long>("speaking.sessions.created");
    public static readonly Counter<long> TurnsCompleted = Meter.CreateCounter<long>("speaking.turns.completed");
    public static readonly Counter<long> TurnsFailed = Meter.CreateCounter<long>("speaking.turns.failed");
    public static readonly Counter<long> SpeechFailures = Meter.CreateCounter<long>("speaking.speech.failures");
    public static readonly Counter<long> FoundryFailures = Meter.CreateCounter<long>("speaking.foundry.failures");
    public static readonly Counter<long> RateLimits = Meter.CreateCounter<long>("speaking.rate_limits");
    public static readonly Histogram<double> TurnDuration = Meter.CreateHistogram<double>(
        "speaking.turn.duration",
        unit: "ms");
    public static readonly Histogram<double> FoundryDuration = Meter.CreateHistogram<double>(
        "speaking.foundry.duration",
        unit: "ms");
}
