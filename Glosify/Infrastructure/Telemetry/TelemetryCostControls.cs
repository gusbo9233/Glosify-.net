using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Glosify.Infrastructure.Telemetry;

internal static class TelemetryCostControls
{
    // Register before UseAzureMonitor so filtering runs before its export processor.
    internal static TracerProviderBuilder AddGlosifyCostControls(this TracerProviderBuilder builder) =>
        builder.AddProcessor(new RoutineDependencyFilter());

    internal static MeterProviderBuilder AddGlosifyCostControls(this MeterProviderBuilder builder) => builder
        .AddView("http.client.open_connections", MetricStreamConfiguration.Drop)
        .AddView("http.client.active_requests", MetricStreamConfiguration.Drop)
        .AddView("http.server.active_requests", MetricStreamConfiguration.Drop);

    private sealed class RoutineDependencyFilter : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity activity)
        {
            // Decide at completion: retain slow queries and failures, including their context.
            if (activity.Status == ActivityStatusCode.Error
                || activity.Events.Any(e => e.Name == "exception"))
                return;

            var sql = activity.Source.Name == "OpenTelemetry.Instrumentation.SqlClient"
                || activity.GetTagItem("db.system") is "mssql"
                || activity.GetTagItem("db.system.name") is "microsoft.sql_server";
            var successfulProbe = activity.Kind == ActivityKind.Server
                && activity.GetTagItem("url.path") is "/healthz" or "/readyz"
                && activity.GetTagItem("http.response.status_code") is int status and >= 200 and < 400;

            if ((sql && activity.Duration < TimeSpan.FromMilliseconds(500)) || successfulProbe)
            {
                // BatchActivityExportProcessor checks Recorded before queueing the span.
                // Changing it only at completion preserves sampling of child/business spans.
                activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
                activity.IsAllDataRequested = false;
            }
        }
    }
}
