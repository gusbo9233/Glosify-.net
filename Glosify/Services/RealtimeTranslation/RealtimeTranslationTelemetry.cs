using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Glosify.Services.RealtimeTranslation;

public static class RealtimeTranslationTelemetry
{
    public const string ActivitySourceName = "Glosify.RealtimeTranslation";
    public const string MeterName = "Glosify.RealtimeTranslation";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);
    public static readonly Counter<long> SessionsCreated = Meter.CreateCounter<long>("translation.sessions.created");
    public static readonly Counter<long> SessionsEnded = Meter.CreateCounter<long>("translation.sessions.ended");
    public static readonly Counter<long> MinutesCharged = Meter.CreateCounter<long>("translation.minutes.charged");
    public static readonly Counter<long> CreditFailures = Meter.CreateCounter<long>("translation.credit.failures");
    public static readonly Counter<long> UpstreamFailures = Meter.CreateCounter<long>("translation.upstream.failures");
    public static readonly Counter<long> Reconnects = Meter.CreateCounter<long>("translation.reconnects");
    public static readonly Counter<long> SessionsStarted = Meter.CreateCounter<long>("translation.sessions.started");
    public static readonly Counter<long> WorkerRecoveries = Meter.CreateCounter<long>("translation.worker.recoveries");
    public static readonly Counter<long> DroppedAudioMilliseconds =
        Meter.CreateCounter<long>("translation.audio.dropped", "ms");
    public static readonly Counter<long> BackpressureEvents =
        Meter.CreateCounter<long>("translation.backpressure.events");
    public static readonly Counter<long> TranslatedCharacters =
        Meter.CreateCounter<long>("translation.characters.translated", "characters");
    public static readonly Histogram<double> FirstCaptionLatency =
        Meter.CreateHistogram<double>("translation.first_caption.latency", "ms");
}
