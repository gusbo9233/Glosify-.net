using System.Diagnostics;
using System.Diagnostics.Metrics;
using Glosify.Infrastructure.Telemetry;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace Glosify.Tests;

public sealed class TelemetryCostControlsTests
{
    [Fact]
    public void Export_keeps_slow_and_failed_SQL_and_business_spans_but_drops_routine_SQL_and_probes()
    {
        using var source = new ActivitySource(Guid.NewGuid().ToString());
        using var exporter = new SpanExporter();
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource(source.Name)
            .AddGlosifyCostControls()
            .AddProcessor(new BatchActivityExportProcessor(exporter))
            .Build();

        void Span(string name, bool sql = false, int milliseconds = 10, bool error = false, string? path = null, int status = 200)
        {
            using var activity = source.StartActivity(name, path is null ? ActivityKind.Client : ActivityKind.Server)!;
            if (sql) activity.SetTag("db.system", "mssql");
            if (path is not null)
                activity.SetTag("url.path", path).SetTag("http.response.status_code", status);
            if (error) activity.SetStatus(ActivityStatusCode.Error);
            activity.SetEndTime(activity.StartTimeUtc.AddMilliseconds(milliseconds));
        }

        Span("idle SQL", sql: true);
        Span("failed SQL", sql: true, error: true);
        Span("slow SQL", sql: true, milliseconds: 500);
        Span("assistant.turn");
        Span("HTTP dependency");
        Span("healthy", path: "/healthz");
        Span("ready", path: "/readyz");
        Span("failed readiness", path: "/readyz", status: 503, error: true);
        Span("other request", path: "/library");
        Assert.True(provider.ForceFlush());
        Assert.Equal(new[] { "failed SQL", "slow SQL", "assistant.turn", "HTTP dependency", "failed readiness", "other request" },
            exporter.Names);
    }

    [Fact]
    public void Metric_export_keeps_latency_and_business_counts_and_drops_connection_gauges()
    {
        using var meter = new Meter(Guid.NewGuid().ToString());
        using var exporter = new MetricExporter();
        using var reader = new BaseExportingMetricReader(exporter);
        using var provider = Sdk.CreateMeterProviderBuilder().AddMeter(meter.Name)
            .AddGlosifyCostControls().AddReader(reader).Build();
        meter.CreateCounter<long>("http.client.open_connections").Add(1);
        meter.CreateCounter<long>("http.client.active_requests").Add(1);
        meter.CreateCounter<long>("http.server.active_requests").Add(1);
        meter.CreateHistogram<double>("http.server.request.duration").Record(0.3);
        meter.CreateCounter<long>("assistant.runtime.model_calls").Add(2);
        Assert.True(provider.ForceFlush());
        Assert.Equal(new[] { "assistant.runtime.model_calls", "http.server.request.duration" }, exporter.Names.Order());
        Assert.Equal(2, exporter.ModelCalls);
    }

    private sealed class SpanExporter : BaseExporter<Activity>
    {
        internal List<string> Names { get; } = [];
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch) Names.Add(activity.DisplayName);
            return ExportResult.Success;
        }
    }

    private sealed class MetricExporter : BaseExporter<Metric>
    {
        internal List<string> Names { get; } = [];
        internal long ModelCalls { get; private set; }
        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                Names.Add(metric.Name);
                if (metric.Name == "assistant.runtime.model_calls")
                    foreach (ref readonly var point in metric.GetMetricPoints()) ModelCalls = point.GetSumLong();
            }
            return ExportResult.Success;
        }
    }
}
