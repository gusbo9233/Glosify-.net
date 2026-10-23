# Assistant analytics operations

The SQL and KQL files in this directory are the maintained analysis query packs for assistant analytics.

Operational analytics capture excludes raw model requests, responses, tool arguments,
and tool results by default. Those database columns contain `{}` unless an operator has
made an explicit privacy and retention decision and set
`AssistantAnalytics:CaptureContent` to `true`. Azure Monitor agent telemetry likewise
keeps sensitive-data capture disabled; tool spans contain names and outcomes, not their
arguments or results.

Completed invocation and tool records are submitted as one turn-level batch to a bounded
in-process background queue. Queue saturation or analytics-database failures are logged
and may drop analytics, but they do not fail or delay the learner's completed assistant
turn. This telemetry is intentionally best-effort rather than a business-critical audit
record.

## Keeping Azure Monitor ingestion small

The October 2026 investigation found roughly 710,000 SQL dependency spans per day,
mostly associated with idle assistant polling, and three HTTP connection/activity
gauges producing about 39 MB/day. The application now filters SQL spans shorter
than 500 ms unless they report an error or exception, and successful `/healthz`
and `/readyz` request spans. Filtering runs at span completion before export;
assistant/model/tool traces retain 100% capture. Failed probes, slow SQL, request
and dependency failures, latency metrics, and application business metrics remain.
The three dropped instruments are `http.client.open_connections`,
`http.client.active_requests`, and `http.server.active_requests`.

Idle assistant workers wait 1, 2, 4, 8, 16, then at most 30 seconds between checks.
Run commits and commands wake all local workers immediately. The wakeup is captured
before checking SQL to avoid missing a concurrent commit; periodic checks also
recover missed notifications, expired leases, retries, and evaluation settlements
even when new evaluations are disabled. Active runs and evaluation queues do not
back off. Scoped database services are disposed before waiting. These local wakeups
follow the single-instance deployment decision in ADR 0001.

Run [cost-monitoring.kql](cost-monitoring.kql) in the workspace after deployment.
The target is below **100 MB per complete UTC day**. Allow for ingestion and billing
delays; confirm multiple full days before claiming a sustained reduction. The
observed 5 GB free monthly allowance is shared at billing-account level, so other
workspaces can consume it too. Existing charges are not reversed. No paid alerts,
workspace caps, pricing tiers, or retention settings are changed by this code.

References: [Microsoft telemetry filtering](https://learn.microsoft.com/azure/azure-monitor/app/opentelemetry-filter),
[OpenTelemetry export processor](https://github.com/open-telemetry/opentelemetry-dotnet/blob/core-1.15.3/src/OpenTelemetry/Trace/Processor/BatchActivityExportProcessor.cs).

## Migration and rollback notes

`AddAssistantAnalyticsCapture` adds a non-null pseudonymous subject identifier and a unique index to `AspNetUsers`. SQL Server must populate the identifier for existing rows and build the index, so deployments with a large user table should measure the migration on a restored copy and schedule an appropriate maintenance window. The current production population is only 1–2 users, so the expected lock is brief.

Rolling this migration back is not correlation-neutral. The rollback drops the subject identifier and all queued telemetry-deletion work. Reapplying the migration creates new subject identifiers, so telemetry emitted under the previous identifiers can no longer be joined or automatically purged by subject. Treat rollback as an emergency operation and retain the old subject/trace mappings until any required Azure Monitor purges have completed.

## Telemetry deletion lifecycle

Chat deletion queues one unique purge record per Azure Monitor table and correlation dimension. The worker atomically claims pending rows with a conditional database update and a per-batch lease ID before submitting them. A second worker cannot claim those rows, while an expired lease is returned to the retry queue after a crash. The worker validates that Azure's status URL remains under the configured Log Analytics workspace and polls until Azure reports `completed`. Transient submission and polling failures use bounded retries; exhausted or permanent failures remain in SQL with status `failed` for operator review.
