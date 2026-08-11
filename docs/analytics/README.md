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

## Migration and rollback notes

`AddAssistantAnalyticsCapture` adds a non-null pseudonymous subject identifier and a unique index to `AspNetUsers`. SQL Server must populate the identifier for existing rows and build the index, so deployments with a large user table should measure the migration on a restored copy and schedule an appropriate maintenance window. The current production population is only 1–2 users, so the expected lock is brief.

Rolling this migration back is not correlation-neutral. The rollback drops the subject identifier and all queued telemetry-deletion work. Reapplying the migration creates new subject identifiers, so telemetry emitted under the previous identifiers can no longer be joined or automatically purged by subject. Treat rollback as an emergency operation and retain the old subject/trace mappings until any required Azure Monitor purges have completed.

## Telemetry deletion lifecycle

Chat deletion queues one unique purge record per Azure Monitor table and correlation dimension. The worker atomically claims pending rows with a conditional database update and a per-batch lease ID before submitting them. A second worker cannot claim those rows, while an expired lease is returned to the retry queue after a crash. The worker validates that Azure's status URL remains under the configured Log Analytics workspace and polls until Azure reports `completed`. Transient submission and polling failures use bounded retries; exhausted or permanent failures remain in SQL with status `failed` for operator review.
