using Glosify.Data;
using Glosify.Services.Abuse;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Ai.Assistant.Runtime;

internal sealed class AssistantTaskWorker(IServiceScopeFactory scopes, IOptions<AssistantRuntimeOptions> options,
    ILogger<AssistantTaskWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, 2).Select(_ => WorkAsync(stoppingToken)));

    private async Task WorkAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!options.Value.Enabled) { await Task.Delay(TimeSpan.FromSeconds(5), ct); continue; }
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<AssistantTaskStore>();
                var claim = await store.ClaimAsync(ct);
                if (claim is null)
                {
                    await scope.ServiceProvider.GetRequiredService<AssistantTaskEvaluationWorker>().EvaluateOneAsync(ct);
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    continue;
                }
                using var step = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var (id, lease) = claim.Value;
                var heartbeat = HeartbeatAsync(id, step, async token =>
                {
                    await using var renewal = scopes.CreateAsyncScope();
                    return await renewal.ServiceProvider.GetRequiredService<AssistantTaskStore>().RenewAsync(id, lease, token);
                }, TimeSpan.FromSeconds(20), logger);
                try
                {
                    await scope.ServiceProvider.GetRequiredService<AssistantTaskExecutor>().StepAsync(id, lease, step.Token);
                }
                catch (OperationCanceledException) when (step.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    // The lease was stopped, reassigned, or could not be renewed. Checkpoints
                    // are fenced by the lease, so nothing from the abandoned step commits.
                }
                finally
                {
                    await step.CancelAsync();
                    await heartbeat; // Never throws, so the reservation release below always runs.
                    // Worker scopes have no HTTP middleware to release provider storage reservations.
                    await scope.ServiceProvider.GetRequiredService<RequestResourceReservations>()
                        .ReleaseAsync(scope.ServiceProvider.GetRequiredService<ResourceQuotaService>());
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Assistant worker iteration failed; durable checkpoints are retained");
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
    }
    /// <summary>
    /// Renews the step's lease until the step ends. Stops the step when the lease is gone, or
    /// when renewal keeps failing: an expired lease lets another worker claim the task and
    /// repeat a paid provider request while this one is still running.
    /// </summary>
    internal static async Task HeartbeatAsync(Guid id, CancellationTokenSource step,
        Func<CancellationToken, Task<int>> renew, TimeSpan interval, ILogger logger)
    {
        // Two-minute leases renewed every 20 seconds: three failures in a row still leave
        // about a minute before another worker could claim the task.
        const int MaxConsecutiveFailures = 3;
        var failures = 0;
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(step.Token))
            {
                try
                {
                    if (await renew(step.Token) == 0) { await step.CancelAsync(); return; }
                    failures = 0;
                }
                catch (Exception ex) when (!step.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Assistant task {TaskId} lease renewal failed", id);
                    if (++failures < MaxConsecutiveFailures) continue;
                    await step.CancelAsync();
                    return;
                }
            }
        }
        // SQL Server can surface cancellation as a SqlException or a wrapped exception.
        catch (Exception) when (step.IsCancellationRequested) { }
    }
}

internal sealed class AssistantTaskEvaluationWorker(GlosifyContext db, AssistantTaskStore store,
    IToolUseEvaluator evaluator, IOptions<JevOptions> jev, IOptions<AssistantRuntimeOptions> options,
    IAiCreditService credits, IOptions<AiUsageOptions> usage)
{
    public async Task EvaluateOneAsync(CancellationToken ct)
    {
        if (!jev.Value.Enabled) return;
        var now = store.Now;
        var task = await db.AssistantTasks.AsNoTracking().Where(x => x.Status == "completed" && x.LeaseUntil <= now
            && x.ModelCalls < options.Value.MaxModelCalls && x.Tokens < options.Value.MaxTokens
            && x.WindowStartedAt.AddSeconds(options.Value.WindowSeconds) > now
            && db.AssistantTaskCalls.Any(c => c.TaskId == x.Id && c.EvaluationStatus == "pending"))
            .OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (task is null) return;
        var lease = Guid.NewGuid();
        if (await db.AssistantTasks.Where(x => x.Id == task.Id && x.LeaseUntil <= now && x.Status == "completed")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseId, lease).SetProperty(x => x.LeaseUntil, now.AddMinutes(2)), ct) == 0) return;
        var call = await db.AssistantTaskCalls.Where(x => x.TaskId == task.Id && x.EvaluationStatus == "pending").OrderBy(x => x.Sequence).FirstAsync(ct);
        var snapshot = RuntimeJson.Read<ToolDecisionSnapshot>(call.SnapshotJson) with { ExecutionResult = call.ResultJson };
        var estimate = RuntimeJson.Write(JevToolUseEvaluator.BuildState(snapshot)).Length / 3 + 1500;
        if (task.Tokens + estimate > options.Value.MaxTokens)
        {
            await db.AssistantTasks.Where(x => x.Id == task.Id && x.LeaseId == lease)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseId, (Guid?)null).SetProperty(x => x.LeaseUntil, now)
                    .SetProperty(x => x.Tokens, options.Value.MaxTokens), ct);
            return;
        }
        // Reserve the shared request/token budget before an advisory network call.
        await db.AssistantTasks.Where(x => x.Id == task.Id && x.LeaseId == lease)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ModelCalls, x => x.ModelCalls + 1)
                .SetProperty(x => x.Tokens, x => x.Tokens + estimate), ct);
        ToolUseEvaluation result;
        if (usage.Value.MonthlyBudget.Enabled && (!usage.Value.MonthlyBudget.MetersProvider("typesafe")
            || !usage.Value.MonthlyBudget.HasTokenPrice(jev.Value.Model)))
        {
            result = new("unavailable", jev.Value.Model, JevToolUseEvaluator.RubricVersion, []);
        }
        else
        {
            Guid? reservationId = null;
            try
            {
                var reservation = await credits.ReserveAsync(new AiUsageContext(task.UserId, AiUsageFeatures.Assistant,
                    "tool_use_evaluation", Guid.NewGuid(), "assistant_task", task.Id.ToString()), "typesafe", jev.Value.Model, estimate, ct);
                reservationId = reservation.ReservationId;
                result = await evaluator.EvaluateAsync(snapshot, ct);
                if (result.Status == "evaluated")
                    await credits.CommitUsageAsync(reservation.ReservationId, new AiTokenUsage(result.Tokens, 0, 0, 0, result.Tokens), ct);
                else await credits.ReleaseAsync(reservation.ReservationId, CancellationToken.None);
                reservationId = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                // Credit/budget outages affect only advisory review, never completed work.
                result = new("unavailable", jev.Value.Model, JevToolUseEvaluator.RubricVersion, []);
            }
            finally
            {
                if (reservationId is Guid outstanding) await credits.ReleaseAsync(outstanding, CancellationToken.None);
            }
        }
        AssistantRuntimeTelemetry.Evaluations.Add(1, new KeyValuePair<string, object?>("status", result.Status));
        call.EvaluationStatus = result.Status;
        call.EvaluationJson = RuntimeJson.Write(result);
        await db.SaveChangesAsync(ct);
        await db.AssistantTasks.Where(x => x.Id == task.Id && x.LeaseId == lease)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseId, (Guid?)null).SetProperty(x => x.LeaseUntil, store.Now)
                .SetProperty(x => x.Revision, x => x.Revision + 1)
                .SetProperty(x => x.Tokens, x => x.Tokens + Math.Max(0, result.Tokens - estimate)), ct);
    }
}
