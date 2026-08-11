/*
Glosify assistant analytics query pack (Azure SQL)

Set the parameters below before running a turn-level query. Content capture begins at
the AddAssistantAnalyticsCapture deployment; legacy rows intentionally remain nullable.
BudgetAmountMicros on usage_debit is the price-card estimate for one model invocation.
Provider responses with confirmed usage are recorded as normal learner usage even when
the application later rejects the response.
Raw invocation and tool payload columns contain {} when the default
AssistantAnalytics:CaptureContent=false setting is used.
*/
DECLARE @From datetimeoffset = DATEADD(day, -30, SYSDATETIMEOFFSET());
DECLARE @To datetimeoffset = SYSDATETIMEOFFSET();
DECLARE @TurnId uniqueidentifier = NULL;

/* Turn volume, completion, cancellation, and failures. */
SELECT
    CAST(started_at AS date) AS [day],
    profile,
    provider,
    actual_model,
    COUNT_BIG(*) AS turns,
    SUM(CASE WHEN status = 'completed' THEN 1 ELSE 0 END) AS completed,
    SUM(CASE WHEN status = 'failed' THEN 1 ELSE 0 END) AS failed,
    SUM(CASE WHEN status = 'cancelled' THEN 1 ELSE 0 END) AS cancelled,
    CAST(100.0 * SUM(CASE WHEN status = 'completed' THEN 1 ELSE 0 END) / NULLIF(COUNT_BIG(*), 0) AS decimal(5,2)) AS completion_rate_pct
FROM assistant_turns
WHERE started_at >= @From AND started_at < @To
GROUP BY CAST(started_at AS date), profile, provider, actual_model
ORDER BY [day] DESC, profile, provider, actual_model;

SELECT error_category, COUNT_BIG(*) AS failures
FROM assistant_turns
WHERE started_at >= @From AND started_at < @To
  AND status IN ('failed', 'cancelled')
GROUP BY error_category
ORDER BY failures DESC;

/* Full, ordered input/output inspection for one turn. */
SELECT
    t.id AS turn_id,
    t.status AS turn_status,
    t.trace_id,
    m.sequence,
    m.role,
    m.content_json,
    m.pending_changes_json,
    m.status AS message_status,
    m.created_at
FROM assistant_turns t
JOIN assistant_messages m ON m.turn_id = t.id
WHERE t.id = @TurnId
ORDER BY m.sequence;

SELECT
    sequence,
    id AS invocation_id,
    agent_name,
    agent_version,
    provider,
    requested_model,
    actual_model,
    request_json,
    response_json,
    provider_response_id,
    status,
    error_category,
    prompt_tokens,
    candidate_tokens,
    thought_tokens,
    tool_prompt_tokens,
    total_tokens,
    duration_ms,
    trace_id,
    span_id
FROM assistant_model_invocations
WHERE turn_id = @TurnId
ORDER BY sequence;

SELECT
    sequence,
    id AS tool_execution_id,
    invocation_id,
    tool_name,
    arguments_json,
    result_json,
    status,
    error_category,
    duration_ms,
    proposed_change_count
FROM assistant_tool_executions
WHERE turn_id = @TurnId
ORDER BY sequence;

/* Token and estimated price-card cost totals. SEK = micros / 1,000,000. */
WITH invocation_costs AS (
    SELECT
        OperationId,
        SUM(COALESCE(BudgetAmountMicros, 0)) AS budget_amount_micros
    FROM AiCreditTransactions
    WHERE Kind = 'usage_debit'
    GROUP BY OperationId
)
SELECT
    t.id AS turn_id,
    th.user_id,
    t.profile,
    COALESCE(i.provider, t.provider) AS provider,
    COALESCE(i.actual_model, t.actual_model) AS model,
    COUNT(DISTINCT i.id) AS model_calls,
    SUM(COALESCE(i.prompt_tokens, 0)) AS prompt_tokens,
    SUM(COALESCE(i.candidate_tokens, 0)) AS candidate_tokens,
    SUM(COALESCE(i.total_tokens, 0)) AS total_tokens,
    SUM(COALESCE(tx.budget_amount_micros, 0)) AS estimated_cost_micros,
    CAST(SUM(COALESCE(tx.budget_amount_micros, 0)) / 1000000.0 AS decimal(18,6)) AS estimated_cost_sek
FROM assistant_turns t
JOIN assistant_threads th ON th.id = t.thread_id
LEFT JOIN assistant_model_invocations i ON i.turn_id = t.id
LEFT JOIN invocation_costs tx ON tx.OperationId = i.id
WHERE t.started_at >= @From AND t.started_at < @To
GROUP BY t.id, th.user_id, t.profile, COALESCE(i.provider, t.provider), COALESCE(i.actual_model, t.actual_model)
ORDER BY t.id;

/* Server, client, provider, and tool latency percentiles. */
WITH latency AS (
    SELECT 'server' AS stage, server_duration_ms AS duration_ms
    FROM assistant_turns
    WHERE started_at >= @From AND started_at < @To
    UNION ALL
    SELECT 'client', client_duration_ms
    FROM assistant_turns
    WHERE started_at >= @From AND started_at < @To
    UNION ALL
    SELECT 'provider', duration_ms
    FROM assistant_model_invocations
    WHERE started_at >= @From AND started_at < @To
    UNION ALL
    SELECT 'tool', duration_ms
    FROM assistant_tool_executions
    WHERE started_at >= @From AND started_at < @To
), percentiles AS (
    SELECT DISTINCT
        stage,
        COUNT(*) OVER (PARTITION BY stage) AS samples,
        PERCENTILE_CONT(0.50) WITHIN GROUP (ORDER BY duration_ms) OVER (PARTITION BY stage) AS p50_ms,
        PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY duration_ms) OVER (PARTITION BY stage) AS p95_ms,
        PERCENTILE_CONT(0.99) WITHIN GROUP (ORDER BY duration_ms) OVER (PARTITION BY stage) AS p99_ms
    FROM latency
    WHERE duration_ms IS NOT NULL
)
SELECT *
FROM percentiles
ORDER BY stage;

/* Tool use and failure rate. */
SELECT
    tool_name,
    COUNT_BIG(*) AS executions,
    SUM(CASE WHEN status = 'completed' THEN 1 ELSE 0 END) AS completed,
    SUM(CASE WHEN status IN ('failed', 'cancelled') THEN 1 ELSE 0 END) AS failed,
    CAST(100.0 * SUM(CASE WHEN status IN ('failed', 'cancelled') THEN 1 ELSE 0 END) / NULLIF(COUNT_BIG(*), 0) AS decimal(5,2)) AS failure_rate_pct,
    AVG(duration_ms) AS avg_duration_ms,
    SUM(proposed_change_count) AS proposed_changes
FROM assistant_tool_executions
WHERE started_at >= @From AND started_at < @To
GROUP BY tool_name
ORDER BY executions DESC;

/* Proposed/applied/rejected outcomes. */
SELECT
    COALESCE(change_outcome, 'none') AS change_outcome,
    COUNT_BIG(*) AS turns,
    SUM(proposed_change_count) AS proposed_changes
FROM assistant_turns
WHERE started_at >= @From AND started_at < @To
GROUP BY COALESCE(change_outcome, 'none')
ORDER BY turns DESC;

/* Feedback coverage, rating, reasons, and comments. */
SELECT
    COUNT_BIG(*) AS completed_turns,
    COUNT_BIG(f.id) AS rated_turns,
    CAST(100.0 * COUNT_BIG(f.id) / NULLIF(COUNT_BIG(*), 0) AS decimal(5,2)) AS feedback_rate_pct,
    CAST(100.0 * SUM(CASE WHEN f.rating = 'up' THEN 1 ELSE 0 END) / NULLIF(COUNT_BIG(f.id), 0) AS decimal(5,2)) AS thumbs_up_rate_pct
FROM assistant_turns t
LEFT JOIN assistant_feedback f ON f.turn_id = t.id
WHERE t.status = 'completed' AND t.started_at >= @From AND t.started_at < @To;

SELECT f.rating, r.reason_code, COUNT_BIG(*) AS selections
FROM assistant_feedback f
JOIN assistant_turns t ON t.id = f.turn_id
JOIN assistant_feedback_reasons r ON r.feedback_id = f.id
WHERE t.started_at >= @From AND t.started_at < @To
GROUP BY f.rating, r.reason_code
ORDER BY f.rating, selections DESC;

SELECT t.id AS turn_id, t.started_at, f.rating, f.comment
FROM assistant_feedback f
JOIN assistant_turns t ON t.id = f.turn_id
WHERE t.started_at >= @From AND t.started_at < @To AND f.comment IS NOT NULL
ORDER BY f.updated_at DESC;

/* Completeness audit: this query should return no rows after cutover. */
SELECT
    t.id AS turn_id,
    t.started_at,
    t.status,
    CONCAT_WS(', ',
        CASE WHEN user_message.id IS NULL THEN 'missing user message' END,
        CASE WHEN t.status = 'completed' AND invocation_stats.invocations = 0 THEN 'missing invocation' END,
        CASE WHEN t.status = 'started' AND t.started_at < DATEADD(minute, -15, @To) THEN 'stale started turn' END,
        CASE WHEN t.status <> 'started' AND t.completed_at IS NULL THEN 'missing completion timestamp' END,
        CASE WHEN t.status = 'completed' AND final_message.id IS NULL THEN 'missing final output' END,
        CASE WHEN t.status = 'completed' AND t.final_message_id IS NULL THEN 'missing final message id' END,
        CASE WHEN invocation_stats.settled_usages < invocation_stats.completed_invocations THEN 'missing usage settlement' END,
        CASE WHEN t.trace_id IS NULL THEN 'missing trace id' END
    ) AS gaps
FROM assistant_turns t
OUTER APPLY (
    SELECT TOP (1) m.id
    FROM assistant_messages m
    WHERE m.turn_id = t.id AND m.role = 'user'
    ORDER BY m.sequence
) user_message
LEFT JOIN assistant_messages final_message ON final_message.id = t.final_message_id AND final_message.turn_id = t.id
OUTER APPLY (
    SELECT
        COUNT_BIG(*) AS invocations,
        SUM(CASE WHEN i.status = 'completed' THEN 1 ELSE 0 END) AS completed_invocations,
        SUM(CASE WHEN tx.Kind = 'usage_debit' THEN 1 ELSE 0 END) AS settled_usages
    FROM assistant_model_invocations i
    LEFT JOIN AiCreditTransactions tx
        ON tx.OperationId = i.id AND tx.Kind = 'usage_debit'
    WHERE i.turn_id = t.id
) invocation_stats
WHERE t.started_at >= @From AND t.started_at < @To
  AND (
      user_message.id IS NULL
      OR (t.status = 'completed' AND invocation_stats.invocations = 0)
      OR (t.status = 'started' AND t.started_at < DATEADD(minute, -15, @To))
      OR (t.status <> 'started' AND t.completed_at IS NULL)
      OR (t.status = 'completed' AND (final_message.id IS NULL OR t.final_message_id IS NULL))
      OR invocation_stats.settled_usages < invocation_stats.completed_invocations
      OR t.trace_id IS NULL
  )
ORDER BY t.started_at DESC;
