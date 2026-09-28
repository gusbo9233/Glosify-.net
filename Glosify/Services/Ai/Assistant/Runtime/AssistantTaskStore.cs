using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Ai.Assistant.Runtime;

public sealed class AssistantTaskConflictException(string message) : InvalidOperationException(message);

public sealed class AssistantTaskStore(
    GlosifyContext db, IOptions<AssistantRuntimeOptions> options, TimeProvider clock)
{
    internal DateTime Now => clock.GetUtcNow().UtcDateTime;
    internal static bool Runnable(string status) => status is "queued" or "running" or "retry_wait";
    internal static bool Terminal(string status) => status is "completed" or "cancelled" or "failed";

    public async Task<AssistantTaskView> StartAsync(Guid threadId, string userId,
        AssistantTaskStartInput input, CancellationToken ct, bool manualApproval = false)
    {
        if (!options.Value.Enabled) throw new AssistantTaskConflictException("Durable assistant execution is not enabled.");
        if (string.IsNullOrWhiteSpace(input.IdempotencyKey) || input.IdempotencyKey.Length > 100
            || string.IsNullOrWhiteSpace(input.Request.Message) || input.Request.Message.Length > 50000)
            throw new ArgumentException("Provide a request of at most 50,000 characters and a valid idempotency key.");
        var requestJson = RuntimeJson.Write(input.Request);
        var hash = RuntimeJson.Hash(RuntimeJson.Write(new { threadId, requestJson, manualApproval }));
        var existing = await db.AssistantTasks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == userId && x.IdempotencyKey == input.IdempotencyKey, ct);
        if (existing is not null)
        {
            if (existing.RequestHash != hash) throw new AssistantTaskConflictException("This idempotency key was already used for another request.");
            return await ViewAsync(existing.Id, userId, ct);
        }
        var thread = await db.AssistantThreads.SingleOrDefaultAsync(x => x.Id == threadId && x.UserId == userId, ct)
            ?? throw new KeyNotFoundException("Chat not found.");
        if (await db.AssistantTasks.AnyAsync(x => x.ActiveUserId == userId, ct))
            throw new AssistantTaskConflictException("An assistant task is already active. Steer, resume, or stop it first.");
        if (thread.ActiveTurnId is not null && thread.ActiveTurnExpiresAt > Now)
            throw new AssistantTaskConflictException("A legacy assistant turn is still running in this chat.");
        var state = new AssistantRuntimeState();
        if (options.Value.CoverageEnabled && input.Request.Message.Length > 4000)
            state.Sources = RuntimeJson.Split(input.Request.Message);
        var task = new AssistantTask
        {
            Id = Guid.NewGuid(), ThreadId = threadId, UserId = userId, ActiveUserId = userId,
            IdempotencyKey = input.IdempotencyKey, RequestHash = hash, RequestJson = requestJson,
            StateJson = RuntimeJson.Write(state), CreatedAt = Now, UpdatedAt = Now,
            WindowStartedAt = Now, ManualApproval = manualApproval,
        };
        db.AssistantTasks.Add(task);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            existing = await db.AssistantTasks.AsNoTracking()
                .SingleOrDefaultAsync(x => x.UserId == userId && x.IdempotencyKey == input.IdempotencyKey, ct);
            if (existing?.RequestHash == hash) return await ViewAsync(existing.Id, userId, ct);
            throw new AssistantTaskConflictException("Another task was submitted concurrently. Reload the active task.");
        }
        return await ViewAsync(task.Id, userId, ct);
    }

    public async Task<AssistantTaskView?> ActiveAsync(Guid threadId, string userId, CancellationToken ct)
    {
        if (!await db.AssistantThreads.AnyAsync(x => x.Id == threadId && x.UserId == userId, ct))
            throw new KeyNotFoundException("Chat not found.");
        var task = await db.AssistantTasks.AsNoTracking().Where(x => x.ThreadId == threadId && x.UserId == userId)
            .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        return task is null ? null : await ViewAsync(task.Id, userId, ct);
    }

    public async Task<AssistantTaskView> ViewAsync(Guid id, string userId, CancellationToken ct)
    {
        var task = await db.AssistantTasks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct)
            ?? throw new KeyNotFoundException("Task not found.");
        var calls = await db.AssistantTaskCalls.AsNoTracking().Where(x => x.TaskId == id).OrderBy(x => x.Sequence).ToListAsync(ct);
        var state = RuntimeJson.Read<AssistantRuntimeState>(task.StateJson);
        var artifactIds = state.DraftQuizzes.Values.ToArray();
        var artifacts = await db.Quizzes.AsNoTracking().Where(x => artifactIds.Contains(x.Id) && x.UserId == userId)
            .Select(x => new AssistantTaskArtifact(x.Id, x.Name, x.ProcessingStatus)).ToListAsync(ct);
        var evaluationBudgetExhausted = task.ModelCalls >= options.Value.MaxModelCalls || task.Tokens >= options.Value.MaxTokens
            || Now >= task.WindowStartedAt.AddSeconds(options.Value.WindowSeconds);
        var proposals = task.Status == "awaiting_approval" ? state.PendingChanges.Where(x => x.Kind == PendingChangeKinds.CreateQuiz
            ? !state.DraftQuizzes.ContainsKey(x.Payload.GetProperty("draft_id").GetString()!)
            : !state.AppliedChanges.Contains(RuntimeJson.Hash(RuntimeJson.Write(x)))).ToList() : [];
        var presenter = new AssistantMessagePresenter();
        var wordIds = presenter.GetReferencedWordIds(proposals);
        var labels = await db.Words.AsNoTracking().Where(x => wordIds.Contains(x.Id) && db.Quizzes.Any(q => q.Id == x.QuizId && q.UserId == userId))
            .Select(x => new AssistantWordLabel(x.Id, x.Lemma, x.Translation)).ToDictionaryAsync(x => x.Id, ct);
        return new(task.Id, task.ThreadId, task.Status, task.Reason, task.Revision, task.SavedChanges,
            task.ModelCalls, task.Tokens, task.ResultJson is null ? null : RuntimeJson.Read<object>(task.ResultJson),
            calls.Select(x => new AssistantTaskActivity(x.Id, x.ToolName, x.Status, x.EvaluationStatus == "pending" && evaluationBudgetExhausted ? "pending_budget" : x.EvaluationStatus,
                x.EvaluationJson is null ? null : RuntimeJson.Read<object>(x.EvaluationJson))).ToArray(),
            calls.Count(x => x.EvaluationStatus == "evaluated"), calls.Count,
            proposals.Select(x => presenter.PresentPendingChange(x, labels)).ToList(), artifacts);
    }

    public async Task<AssistantTaskView> CommandAsync(Guid id, string userId, string command,
        AssistantTaskCommand input, CancellationToken ct)
    {
        var commandKey = RuntimeJson.Hash(RuntimeJson.Write(new { command, input }));
        // Stop and steering mean the same thing at any revision, while a running task advances
        // its revision at every checkpoint. They apply to the latest row, retried when the
        // worker commits first, so the user never has to race the worker. Approval and
        // Resume stay bound to the revision whose proposal or pause the user saw.
        var latestRevision = command is "cancel" or "steer";
        for (var attempt = 1; ; attempt++)
        {
            try { return await TryCommandAsync(id, userId, command, input, commandKey, latestRevision, ct); }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
                var latest = await OwnedAsync(id, userId, ct);
                if (RuntimeJson.Read<AssistantRuntimeState>(latest.StateJson).AppliedCommands.Contains(commandKey))
                    return await ViewAsync(id, userId, ct);
                if (!latestRevision || attempt == 5)
                    throw new AssistantTaskConflictException("The task changed. Reload its current status.");
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<AssistantTaskView> TryCommandAsync(Guid id, string userId, string command,
        AssistantTaskCommand input, string commandKey, bool latestRevision, CancellationToken ct)
    {
        var task = await OwnedAsync(id, userId, ct);
        var state = RuntimeJson.Read<AssistantRuntimeState>(task.StateJson);
        if (state.AppliedCommands.Contains(commandKey) || command == "cancel" && Terminal(task.Status))
            return await ViewAsync(id, userId, ct);
        if (!latestRevision && task.Revision != input.Revision)
            throw new AssistantTaskConflictException("The task changed. Reload its current status.");
        switch (command)
        {
            case "cancel":
                task.Status = "cancelled";
                task.Reason = "Stopped. Saved changes have been retained.";
                task.ActiveUserId = null;
                task.LeaseId = null;
                var ids = state.DraftQuizzes.Values.ToArray();
                foreach (var quiz in await db.Quizzes.Where(x => ids.Contains(x.Id) && x.UserId == userId).ToListAsync(ct))
                { quiz.ProcessingStatus = "Incomplete"; quiz.ProcessingMessage = task.Reason; }
                break;
            case "resume" when task.Status is "paused" or "awaiting_input":
                task.Status = task.RetryAt > Now ? "retry_wait" : "queued";
                task.WindowStartedAt = Now;
                task.ModelCalls = task.Tokens = task.Failures = 0;
                state.NoProgress = 0;
                task.Reason = null;
                if (task.RetryAt <= Now) task.RetryAt = null;
                break;
            case "approve" when task.Status == "awaiting_approval":
                task.ApprovalGranted = true;
                task.Status = "queued";
                break;
            case "steer" when !Terminal(task.Status):
                if (string.IsNullOrWhiteSpace(input.Message) || input.Message.Length > 50000)
                    throw new ArgumentException("Provide steering of at most 50,000 characters.");
                var steering = RuntimeJson.Read<List<string>>(task.SteeringJson);
                steering.Add(input.Message);
                task.SteeringJson = RuntimeJson.Write(steering);
                // Superseded, uncommitted proposals must not inherit approval.
                task.ApprovalGranted = false;
                if (task.Status is "awaiting_approval" or "awaiting_input") task.Status = "queued";
                break;
            default: throw new AssistantTaskConflictException("That action is not available for this task.");
        }
        state.AppliedCommands.Add(commandKey);
        task.StateJson = RuntimeJson.Write(state);
        task.Revision++;
        task.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return await ViewAsync(id, userId, ct);
    }

    internal async Task<AssistantTask> OwnedAsync(Guid id, string userId, CancellationToken ct) =>
        await db.AssistantTasks.SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct)
            ?? throw new KeyNotFoundException("Task not found.");

    internal async Task<(Guid Id, Guid Lease)?> ClaimAsync(CancellationToken ct)
    {
        var now = Now;
        var candidate = await db.AssistantTasks.AsNoTracking()
            .Where(x => (x.Status == "queued" || x.Status == "running" || x.Status == "retry_wait")
                && (x.RetryAt == null || x.RetryAt <= now) && x.LeaseUntil <= now)
            .OrderBy(x => x.CreatedAt).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        if (candidate is null) return null;
        var lease = Guid.NewGuid();
        var count = await db.AssistantTasks.Where(x => x.Id == candidate && x.LeaseUntil <= now
                && (x.Status == "queued" || x.Status == "running" || x.Status == "retry_wait"))
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.LeaseId, lease)
                .SetProperty(x => x.LeaseUntil, now.AddMinutes(2)).SetProperty(x => x.Status, "running"), ct);
        return count == 1 ? (candidate.Value, lease) : null;
    }

    internal Task<int> RenewAsync(Guid id, Guid lease, CancellationToken ct) => db.AssistantTasks
        .Where(x => x.Id == id && x.LeaseId == lease && x.LeaseUntil > Now && x.Status == "running")
        .ExecuteUpdateAsync(set => set.SetProperty(x => x.LeaseUntil, Now.AddMinutes(2)), ct);
}
