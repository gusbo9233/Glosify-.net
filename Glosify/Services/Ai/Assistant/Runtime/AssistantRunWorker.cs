using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Abuse;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Ai.Assistant.Runtime;

/// <summary>
/// Claims runs and advances them one step at a time, renewing the lease while a step runs.
/// </summary>
internal sealed class AssistantRunWorker(
    IServiceScopeFactory scopes,
    IOptions<AssistantRuntimeOptions> options,
    ILogger<AssistantRunWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, options.Value.Workers).Select(_ => WorkAsync(stoppingToken)));

    private async Task WorkAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<AssistantRunStore>();
                var claim = await store.ClaimAsync(cancellationToken);
                if (claim is null)
                {
                    await scope.ServiceProvider.GetRequiredService<AssistantEvaluationWorker>().EvaluateOneAsync(cancellationToken);
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                    continue;
                }

                var (id, lease) = claim.Value;
                using var step = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var heartbeat = HeartbeatAsync(id, step, async token =>
                {
                    await using var renewal = scopes.CreateAsyncScope();
                    return await renewal.ServiceProvider.GetRequiredService<AssistantRunStore>().RenewAsync(id, lease, token);
                }, TimeSpan.FromSeconds(20), logger);
                try
                {
                    await scope.ServiceProvider.GetRequiredService<AssistantRunExecutor>().StepAsync(id, lease, step.Token);
                }
                catch (OperationCanceledException) when (step.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // The lease was lost or could not be renewed. Checkpoints are fenced by the
                    // lease, so nothing from the abandoned step commits.
                }
                finally
                {
                    await step.CancelAsync();
                    await heartbeat;
                    // Worker scopes have no HTTP middleware to release storage reservations.
                    await scope.ServiceProvider.GetRequiredService<RequestResourceReservations>()
                        .ReleaseAsync(scope.ServiceProvider.GetRequiredService<ResourceQuotaService>());
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Assistant worker iteration failed; saved progress is kept");
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
        }
    }

    /// <summary>
    /// Renews the step's lease until the step ends, and stops the step when the lease is gone
    /// or keeps failing to renew: an expired lease lets another worker repeat a paid call.
    /// </summary>
    internal static async Task HeartbeatAsync(
        Guid id,
        CancellationTokenSource step,
        Func<CancellationToken, Task<int>> renew,
        TimeSpan interval,
        ILogger logger)
    {
        // Two-minute leases renewed every 20 seconds: three failures in a row still leave about
        // a minute before another worker could claim the run.
        const int MaxConsecutiveFailures = 3;
        var failures = 0;
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(step.Token))
            {
                try
                {
                    if (await renew(step.Token) == 0)
                    {
                        await step.CancelAsync();
                        return;
                    }

                    failures = 0;
                }
                catch (Exception ex) when (!step.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Assistant run {RunId} lease renewal failed", id);
                    if (++failures < MaxConsecutiveFailures)
                    {
                        continue;
                    }

                    await step.CancelAsync();
                    return;
                }
            }
        }
        catch (Exception) when (step.IsCancellationRequested)
        {
        }
    }
}

/// <summary>
/// Reviews finished tool calls with the advisory Jev judge while workers are otherwise idle.
/// Reviews never change, block, or undo work.
/// </summary>
internal sealed class AssistantEvaluationWorker(
    GlosifyContext db,
    IToolUseEvaluator evaluator,
    IOptions<JevOptions> jev,
    IAiCreditService credits,
    IOptions<AiUsageOptions> usage,
    TimeProvider clock)
{
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan EvaluationTimeout = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan SettlementTimeout = TimeSpan.FromSeconds(30);
    public async Task EvaluateOneAsync(CancellationToken cancellationToken)
    {
        var allowEvaluation = jev.Value.Enabled;
        var now = clock.GetUtcNow().UtcDateTime;
        var eligible = db.AssistantToolEvaluations.AsNoTracking()
            .Where(candidate => (allowEvaluation || candidate.Status == "settling")
                && (candidate.Status == "pending"
                    || ((candidate.Status == "evaluating" || candidate.Status == "settling")
                        && (candidate.LeaseUntil == null || candidate.LeaseUntil <= now)))
                && db.AssistantRuns.Any(run => run.Id == candidate.RunId
                    && (run.Status == AssistantRunStatus.Completed || run.Status == AssistantRunStatus.Cancelled || run.Status == AssistantRunStatus.Failed)));
        var evaluation = await eligible.OrderBy(candidate => candidate.CreatedAt).ThenBy(candidate => candidate.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (evaluation is null)
        {
            return;
        }

        // Claim/reclaim atomically. A unique lease fences a delayed old worker's result.
        var lease = Guid.NewGuid();
        var resumingSettlement = evaluation.Status == "settling";
        if (await eligible.Where(candidate => candidate.Id == evaluation.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(candidate => candidate.Status, resumingSettlement ? "settling" : "evaluating")
                    .SetProperty(candidate => candidate.LeaseId, lease)
                    .SetProperty(candidate => candidate.LeaseUntil, now + LeaseDuration), cancellationToken) == 0)
        {
            return;
        }

        if (resumingSettlement)
        {
            // The provider already returned and was paid. Recover only the database debit,
            // even after shutdown, timeout or a failed settlement transaction.
            using var recovery = new CancellationTokenSource(SettlementTimeout, clock);
            var retained = RunJson.Read<ToolUseEvaluation>(evaluation.EvaluationJson!);
            var outstanding = await OutstandingReservations().Select(r => r.ReservationId).SingleOrDefaultAsync(recovery.Token);
            await CompleteAsync(retained, outstanding, recovery.Token);
            return;
        }

        using var deadline = new CancellationTokenSource(EvaluationTimeout, clock);
        using var evaluationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = evaluationCancellation.Token;
        Guid? reservationId = null;
        ToolUseEvaluation result;
        // OperationId durably ties reservations to this evaluation even if the process
        // dies before ReserveAsync returns. Retire the previous attempt before reserving
        // again, including when pricing/credits no longer allow another evaluation.
        // Cleanup failures must leave the lease recoverable. They are not provider failures:
        // marking this row unavailable would strand the previous attempt's credit holds.
        if (!await WithOwnedLeaseAsync(async () =>
        {
            var abandoned = await OutstandingReservations().Select(r => r.ReservationId!.Value).ToListAsync(token);
            foreach (var id in abandoned) await credits.ReleaseAsync(id, token);
        }, token)) return;

        try
        {
            var userId = await db.AssistantRuns.Where(run => run.Id == evaluation.RunId).Select(run => run.UserId).SingleAsync(token);
            var snapshot = RunJson.Read<ToolDecisionSnapshot>(evaluation.SnapshotJson);
            var estimate = RunJson.Write(JevToolUseEvaluator.BuildState(snapshot)).Length / 3 + 1500;
            if (usage.Value.MonthlyBudget.Enabled
                && (!usage.Value.MonthlyBudget.MetersProvider("typesafe") || !usage.Value.MonthlyBudget.HasTokenPrice(jev.Value.Model)))
            {
                result = Unavailable();
            }
            else
            {
                if (!await WithOwnedLeaseAsync(async () =>
                {
                    // A transient commit failure may have committed the reservation. Reuse it
                    // when retrying this transaction instead of reserving twice.
                    reservationId = await OutstandingReservations().Select(r => r.ReservationId).SingleOrDefaultAsync(token);
                    if (reservationId is null)
                    {
                        var reservation = await credits.ReserveAsync(
                            new AiUsageContext(userId, AiUsageFeatures.Assistant, "tool_use_evaluation", evaluation.Id, "assistant_run", evaluation.RunId.ToString()),
                            "typesafe", jev.Value.Model, estimate, token);
                        reservationId = reservation.ReservationId;
                    }
                }, token)) return;
                result = await evaluator.EvaluateAsync(snapshot, token);
                if (result.Status == "evaluated" && result.Tokens <= 0) result = Unavailable();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
            // Graceful shutdown retries immediately; a process crash retries on lease expiry.
            using var cleanup = new CancellationTokenSource(SettlementTimeout, clock);
            await OwnedLease().ExecuteUpdateAsync(set => set.SetProperty(e => e.Status, "pending")
                .SetProperty(e => e.LeaseId, (Guid?)null).SetProperty(e => e.LeaseUntil, (DateTime?)null), cleanup.Token);
            if (reservationId is Guid interrupted) await credits.ReleaseAsync(interrupted, cleanup.Token);
            throw;
        }
        catch (Exception)
        {
            db.ChangeTracker.Clear();
            // Provider failures/timeouts are advisory and never block the original run.
            result = Unavailable();
        }

        // The evaluation deadline and host shutdown must not cancel settlement after a
        // successful paid response. Persist the result before debiting so a failed
        // settlement can resume without repeating the provider call or releasing its hold.
        using var settlement = new CancellationTokenSource(SettlementTimeout, clock);
        if (result.Status == "evaluated")
        {
            var json = RunJson.Write(result);
            if (await OwnedLease().ExecuteUpdateAsync(set => set.SetProperty(e => e.Status, "settling")
                    .SetProperty(e => e.EvaluationJson, json)
                    .SetProperty(e => e.LeaseUntil, clock.GetUtcNow().UtcDateTime + LeaseDuration), settlement.Token) == 0)
            {
                if (reservationId is Guid stale) await credits.ReleaseAsync(stale, settlement.Token);
                return;
            }
            // Exceptions deliberately leave the durable result and reservation for recovery.
            await CompleteAsync(result, reservationId, settlement.Token);
        }
        else
        {
            try
            {
                if (await CompleteAsync(result, reservationId, settlement.Token)) reservationId = null;
            }
            finally
            {
                db.ChangeTracker.Clear();
                if (reservationId is Guid unused) await credits.ReleaseAsync(unused, settlement.Token);
            }
        }

        IQueryable<AiCreditTransaction> OutstandingReservations() => db.AiCreditTransactions.AsNoTracking()
            .Where(r => r.OperationId == evaluation.Id && r.Feature == AiUsageFeatures.Assistant
                && r.Operation == "tool_use_evaluation" && r.Kind == AiCreditTransactionKinds.Reservation
                && r.ReservationId != null && !db.AiCreditTransactions.Any(t => t.ReservationId == r.ReservationId
                    && (t.Kind == AiCreditTransactionKinds.UsageDebit || t.Kind == AiCreditTransactionKinds.Release)));

        async Task<bool> WithOwnedLeaseAsync(Func<Task> action, CancellationToken ct) =>
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = db.Database.IsRelational()
                    ? await db.Database.BeginTransactionAsync(ct) : null;
                // A conditional write holds the evaluation row until the credit changes
                // commit. An expired worker cannot reserve after a replacement claimed it.
                if (await OwnedLease().ExecuteUpdateAsync(set => set.SetProperty(e => e.LeaseUntil, e => e.LeaseUntil), ct) == 0)
                    return false;
                await action();
                if (transaction is not null) await transaction.CommitAsync(ct);
                return true;
            });

        async Task<bool> CompleteAsync(ToolUseEvaluation completed, Guid? reservation, CancellationToken ct)
        {
            // AiCreditService shares this scoped context. Fence the lease, save the result and
            // settle its credits in one transaction: a crash can commit all of them or none.
            // Retry database settlement only, never another paid provider call.
            var saved = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = db.Database.IsRelational()
                    ? await db.Database.BeginTransactionAsync(ct) : null;
                var json = RunJson.Write(completed);
                if (await OwnedLease().ExecuteUpdateAsync(set => set.SetProperty(e => e.Status, completed.Status)
                        .SetProperty(e => e.EvaluationJson, json).SetProperty(e => e.LeaseId, (Guid?)null)
                        .SetProperty(e => e.LeaseUntil, (DateTime?)null), ct) == 0) return false;
                if (reservation is Guid id)
                {
                    if (completed.Status == "evaluated")
                        await credits.CommitUsageAsync(id, new AiTokenUsage(completed.Tokens, 0, 0, 0, completed.Tokens), ct);
                    else
                        await credits.ReleaseAsync(id, ct);
                }
                if (transaction is not null) await transaction.CommitAsync(ct);
                return true;
            });
            if (saved) AssistantRunTelemetry.Evaluations.Add(1, new KeyValuePair<string, object?>("status", completed.Status));
            return saved;
        }

        IQueryable<AssistantToolEvaluation> OwnedLease() => db.AssistantToolEvaluations
            .Where(e => e.Id == evaluation.Id && (e.Status == "evaluating" || e.Status == "settling") && e.LeaseId == lease
                && e.LeaseUntil > clock.GetUtcNow().UtcDateTime);
        ToolUseEvaluation Unavailable() => new("unavailable", jev.Value.Model, JevToolUseEvaluator.RubricVersion, []);

    }
}
