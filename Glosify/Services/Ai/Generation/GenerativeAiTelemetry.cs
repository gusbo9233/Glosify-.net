using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Glosify.Services.Ai.Generation;

internal static class GenerativeAiTelemetry
{
    internal const string ActivitySourceName = "Glosify.GenerativeAi";
    internal const string MeterName = "Glosify.GenerativeAi";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    internal static readonly Meter Meter = new(MeterName);
    internal static readonly Counter<long> Requests = Meter.CreateCounter<long>("glosify.ai.requests");
    internal static readonly Counter<long> Failures = Meter.CreateCounter<long>("glosify.ai.failures");
    internal static readonly Counter<long> Throttles = Meter.CreateCounter<long>("glosify.ai.throttles");
    internal static readonly Counter<long> Timeouts = Meter.CreateCounter<long>("glosify.ai.timeouts");
    internal static readonly Counter<long> InputTokens = Meter.CreateCounter<long>("glosify.ai.input_tokens");
    internal static readonly Counter<long> OutputTokens = Meter.CreateCounter<long>("glosify.ai.output_tokens");
    internal static readonly Counter<long> TotalTokens = Meter.CreateCounter<long>("glosify.ai.total_tokens");
    internal static readonly Counter<long> ToolTurns = Meter.CreateCounter<long>("glosify.ai.assistant_tool_turns");
    internal static readonly Counter<long> CreditReservations = Meter.CreateCounter<long>("glosify.ai.credit_reservations");
    internal static readonly Counter<long> CreditCommits = Meter.CreateCounter<long>("glosify.ai.credit_commits");
    internal static readonly Counter<long> CreditReleases = Meter.CreateCounter<long>("glosify.ai.credit_releases");
    internal static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>("glosify.ai.duration", "ms");

    internal static TagList Tags(string feature, string provider, string deployment, string outcome = "success")
    {
        var tags = new TagList
        {
            { "ai.feature", feature },
            { "ai.provider", provider },
            { "ai.deployment", deployment },
            { "ai.outcome", outcome },
        };
        return tags;
    }
}
