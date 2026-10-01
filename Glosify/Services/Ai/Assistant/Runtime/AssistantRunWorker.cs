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
    IOptions<AiUsageOptions> usage)
{
    public async Task EvaluateOneAsync(CancellationToken cancellationToken)
    {
        if (!jev.Value.Enabled)
        {
            return;
        }

        var evaluation = await db.AssistantToolEvaluations
            .Where(candidate => candidate.Status == "pending"
                && db.AssistantRuns.Any(run => run.Id == candidate.RunId
                    && (run.Status == AssistantRunStatus.Completed || run.Status == AssistantRunStatus.Cancelled || run.Status == AssistantRunStatus.Failed)))
            .OrderBy(candidate => candidate.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (evaluation is null)
        {
            return;
        }

        // Claim it first so a second worker does not review the same call.
        if (await db.AssistantToolEvaluations
                .Where(candidate => candidate.Id == evaluation.Id && candidate.Status == "pending")
                .ExecuteUpdateAsync(set => set.SetProperty(candidate => candidate.Status, "evaluating"), cancellationToken) == 0)
        {
            return;
        }

        var userId = await db.AssistantRuns.Where(run => run.Id == evaluation.RunId).Select(run => run.UserId).SingleAsync(cancellationToken);
        var snapshot = RunJson.Read<ToolDecisionSnapshot>(evaluation.SnapshotJson);
        var estimate = RunJson.Write(JevToolUseEvaluator.BuildState(snapshot)).Length / 3 + 1500;
        ToolUseEvaluation result;
        if (usage.Value.MonthlyBudget.Enabled
            && (!usage.Value.MonthlyBudget.MetersProvider("typesafe") || !usage.Value.MonthlyBudget.HasTokenPrice(jev.Value.Model)))
        {
            result = new ToolUseEvaluation("unavailable", jev.Value.Model, JevToolUseEvaluator.RubricVersion, []);
        }
        else
        {
            Guid? reservationId = null;
            try
            {
                var reservation = await credits.ReserveAsync(
                    new AiUsageContext(userId, AiUsageFeatures.Assistant, "tool_use_evaluation", Guid.NewGuid(), "assistant_run", evaluation.RunId.ToString()),
                    "typesafe",
                    jev.Value.Model,
                    estimate,
                    cancellationToken);
                reservationId = reservation.ReservationId;
                result = await evaluator.EvaluateAsync(snapshot, cancellationToken);
                if (result.Status == "evaluated")
                {
                    await credits.CommitUsageAsync(reservation.ReservationId, new AiTokenUsage(result.Tokens, 0, 0, 0, result.Tokens), cancellationToken);
                }
                else
                {
                    await credits.ReleaseAsync(reservation.ReservationId, CancellationToken.None);
                }

                reservationId = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Credit or budget outages only leave the review unavailable.
                result = new ToolUseEvaluation("unavailable", jev.Value.Model, JevToolUseEvaluator.RubricVersion, []);
            }
            finally
            {
                if (reservationId is Guid outstanding)
                {
                    await credits.ReleaseAsync(outstanding, CancellationToken.None);
                }
            }
        }

        AssistantRunTelemetry.Evaluations.Add(1, new KeyValuePair<string, object?>("status", result.Status));
        evaluation.Status = result.Status;
        evaluation.EvaluationJson = RunJson.Write(result);
        await db.SaveChangesAsync(cancellationToken);
    }
}
