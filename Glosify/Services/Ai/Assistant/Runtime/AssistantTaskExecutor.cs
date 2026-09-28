using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Ai.Assistant.Runtime;

internal sealed class AssistantTaskExecutor(GlosifyContext db, AssistantTaskStore store,
    AssistantRuntimeContext contexts, IGenerativeAiClient model, IAssistantTools tools,
    IChangeApplier applier, AssistantThreadStore threads, AssistantMessagePresenter presenter,
    IOptions<AssistantRuntimeOptions> options, IOptions<JevOptions> jev, ILogger<AssistantTaskExecutor> logger,
    AssistantReadPrefetcher? prefetcher = null)
{
    public async Task StepAsync(Guid id, Guid lease, CancellationToken ct)
    {
        try { await StepCoreAsync(id, lease, ct); }
        catch (DbUpdateConcurrencyException)
        {
            // Steering or Stop committed between this step's read and its checkpoint, so the
            // checkpoint rolled back. Hand the task back now rather than when the lease
            // expires. A Stop already cleared the lease, and then nothing changes here.
            db.ChangeTracker.Clear();
            var now = store.Now;
            await db.AssistantTasks.Where(x => x.Id == id && x.LeaseId == lease)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseId, (Guid?)null).SetProperty(x => x.LeaseUntil, now), ct);
        }
    }

    private async Task StepCoreAsync(Guid id, Guid lease, CancellationToken ct)
    {
        var task = await LoadAsync(id, lease, ct);
        if (task is null) return;
        var state = RuntimeJson.Read<AssistantRuntimeState>(task.StateJson);
        if (!state.Initialized)
        {
            await AtomicAsync(id, lease, async (t, s) =>
            {
                var input = RuntimeJson.Read<AssistantTaskInput>(t.RequestJson);
                var previous = await threads.LoadMessagesAsync(t.ThreadId, ct);
                // Only the conversation's text messages from earlier turns: both sides, so a
                // follow-up can refer to an earlier answer. Legacy turns store tool calls and
                // results as separate messages; replaying one without its partner is rejected.
                s.History = previous.Where(x => !string.IsNullOrWhiteSpace(presenter.ExtractVisibleText(x)))
                    .TakeLast(12).Select(x => new AgentTurn(x.Role, x.ContentJson)).ToList();
                s.History.Add(RuntimeJson.Text("user", s.Sources.Count == 0 ? input.Message
                    : input.Message[..Math.Min(1500, input.Message.Length)] + "\n[The complete request is stored in source sections. Read every section before finishing.]"));
                s.RequestIndex = s.History.Count - 1;
                db.AssistantTurns.Add(new AssistantTurn
                {
                    Id = s.TurnId, ThreadId = t.ThreadId, Profile = "Durable", RequestedModel = OpenAiModels.Luna,
                    Status = AssistantTurnStatus.Started, StartedAt = new DateTimeOffset(store.Now, TimeSpan.Zero),
                });
                await AddMessageAsync(t, s, "user", input.Message, null, ct);
                var thread = await db.AssistantThreads.SingleAsync(x => x.Id == t.ThreadId, ct);
                if (thread.Title == AssistantThreadDefaults.NewChatTitle) thread.Title = presenter.NormalizeTitle(input.Message);
                s.Initialized = true;
                t.WindowStartedAt = store.Now;
            }, ct);
            return;
        }
        if (state.NextCall >= state.Calls.Count && (task.ModelCalls >= options.Value.MaxModelCalls || task.Tokens >= options.Value.MaxTokens
            || store.Now >= task.WindowStartedAt.AddSeconds(options.Value.WindowSeconds)))
        {
            await AtomicAsync(id, lease, (t, s) => PauseAsync(t, s, "Execution budget reached. Resume to continue from saved progress.", ct), ct);
            return;
        }
        if (state.NoProgress >= 6)
        {
            await AtomicAsync(id, lease, (t, s) => PauseAsync(t, s, "Repeated steps made no progress. Review the latest error and steer or resume the task.", ct), ct);
            return;
        }
        var steering = RuntimeJson.Read<List<string>>(task.SteeringJson);
        if (steering.Count != state.SteeringCount)
        {
            await AtomicAsync(id, lease, async (t, s) =>
            {
                var updates = RuntimeJson.Read<List<string>>(t.SteeringJson);
                await SupersedeRemainingAsync(t.Id, s, "User steering superseded this call.", ct);
                foreach (var update in updates.Skip(s.SteeringCount))
                {
                    s.History.Add(RuntimeJson.Text("user", update));
                    await AddMessageAsync(t, s, "user", update, null, ct);
                }
                s.SteeringCount = updates.Count;
                s.PendingChanges.RemoveAll(x => x.Kind != PendingChangeKinds.CreateQuiz || !s.DraftQuizzes.ContainsKey(x.Payload.GetProperty("draft_id").GetString()!));
                // A deferred move is still an uncommitted proposal. Steering must not
                // silently restore it at finish after the user changed the destination.
                s.DraftCollections.Clear();
                s.NoProgress = 0;
                s.NeedsCorrection = false;
                s.UnresolvedMutations.Clear();
                t.ApprovalGranted = false;
            }, ct);
            return;
        }
        try
        {
            if (state.NextCall < state.Calls.Count)
            {
                await PrefetchAsync(task, state, lease, ct);
                await ExecuteCallAsync(id, lease, ct);
            }
            else await InvokeAsync(task, state, lease, ct);
        }
        catch (DbUpdateConcurrencyException) { throw; } // Steering/Stop won; StepAsync releases the lease.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Assistant task {TaskId} step failed", id);
            db.ChangeTracker.Clear();
            await AtomicAsync(id, lease, async (t, s) =>
            {
                foreach (var attempt in await db.AssistantTaskAttempts.Where(x => x.TaskId == id && x.Status == "started").ToListAsync(ct))
                { attempt.Status = "failed"; attempt.ErrorCategory = ex.GetType().Name; attempt.CompletedAt = store.Now; }
                t.Failures++;
                if (ex is GenerativeAiStructuredOutputException)
                {
                    s.NoProgress++;
                    s.History.Add(RuntimeJson.Text("user", "The provider response was incomplete or invalid; no tool arguments were executed. Reduce batch size and retry only unfinished work. Do not ask the user to split the input."));
                    t.Status = "queued";
                    t.Reason = "Adjusting the batch after an incomplete model response.";
                    return;
                }
                if (Transient(ex) && t.Failures < 3)
                {
                    t.Status = "retry_wait";
                    t.RetryAt = store.Now.Add(RetryDelay(ex, t.Failures));
                    t.Reason = "A temporary service error occurred. Retrying from saved progress.";
                    if (t.RetryAt >= t.WindowStartedAt.AddSeconds(options.Value.WindowSeconds))
                        await PauseAsync(t, s, $"The provider requested waiting until {t.RetryAt:O}, beyond this execution window. Resume to continue after that time.", ct);
                }
                else if (ex is ArgumentException or InvalidOperationException && ex is not (InsufficientAiCreditsException or MonthlyAiBudgetExceededException)
                    && s.NextCall < s.Calls.Count && t.Failures < 3)
                {
                    var failedCall = s.Calls[s.NextCall];
                    var outcome = new AssistantToolOutcome("correctable", Error: "The operation failed and its changes were rolled back. Re-read the target and correct the call before continuing.");
                    var journal = await db.AssistantTaskCalls.SingleOrDefaultAsync(x => x.TaskId == id && x.Sequence == s.CallSequence, ct);
                    if (journal is not null) { journal.Status = "correctable"; journal.ResultJson = RuntimeJson.Write(outcome); }
                    s.CallResults.Add(ResponsePart(failedCall, outcome));
                    RuntimeMutationCorrections.Observe(s, tools.ResolveCanonicalName(failedCall.Name) ?? failedCall.Name, failedCall.ArgsJson, outcome, s.CallSequence);
                    s.NextCall++; s.CallSequence++; s.NoProgress++;
                    if (s.NextCall == s.Calls.Count) FlushResults(s);
                    t.Status = "queued";
                    t.Reason = "Correcting a failed tool call; earlier saved changes are retained.";
                }
                else await PauseAsync(t, s, ex is InsufficientAiCreditsException ? "More AI credits are needed. Saved progress is retained."
                    : ex is MonthlyAiBudgetExceededException ? "The application AI budget is exhausted. Saved progress is retained."
                    : "The step could not finish. Saved progress is retained; resume after resolving the service or account issue.", ct);
            }, ct);
        }
    }

    private async Task InvokeAsync(AssistantTask task, AssistantRuntimeState state, Guid lease, CancellationToken ct)
    {
        var runtime = await contexts.BuildAsync(task, state, ct);
        Compact(state);
        runtime = runtime with { Request = runtime.Request with { History = state.History } };
        var estimate = RuntimeJson.EstimateTokens(runtime.Request);
        if (task.Tokens + estimate + 2048 > options.Value.MaxTokens)
        {
            await AtomicAsync(task.Id, lease, (t, s) => PauseAsync(t, s, "Token budget reached. Resume to continue.", ct), ct);
            return;
        }
        runtime = runtime with { Request = runtime.Request with { MaxOutputTokens = Math.Min(8192, options.Value.MaxTokens - task.Tokens - estimate) } };
        var fingerprint = await FingerprintAsync(runtime.Tools.QuizId, ct);
        var invocationId = Guid.NewGuid();
        var revision = task.Revision;
        // Count attempts before contacting the provider, including lost responses and crashes.
        await AtomicAsync(task.Id, lease, (t, s) =>
        {
            db.AssistantTaskAttempts.Add(new AssistantTaskAttempt
            {
                Id = invocationId, TaskId = t.Id, RequestJson = RuntimeJson.Write(runtime.Request), StartedAt = store.Now,
            });
            t.ModelCalls++;
            t.Tokens += estimate;
            s.ResourceFingerprint = fingerprint;
            // Keep the checkpoint as compact as the request; only this lease holder edits history.
            s.History = [.. state.History];
            s.RequestIndex = state.RequestIndex;
            return Task.CompletedTask;
        }, ct, release: false);
        db.ChangeTracker.Clear();
        task = (await LoadAsync(task.Id, lease, ct))!;
        if (task is null) return;
        revision = task.Revision;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.Value.WindowSeconds - (store.Now - task.WindowStartedAt).TotalSeconds)));
        AssistantRuntimeTelemetry.ModelAttempts.Add(1);
        var response = await model.RunAgentTurnAsync(runtime.Request,
            new AiUsageContext(task.UserId, AiUsageFeatures.Assistant, "assistant_task", invocationId,
                "assistant_task", task.Id.ToString(), state.TurnId), timeout.Token);
        await AtomicAsync(task.Id, lease, async (t, s) =>
        {
            var attempt = await db.AssistantTaskAttempts.SingleAsync(x => x.Id == invocationId, ct);
            attempt.Status = "completed";
            attempt.ResponseJson = RuntimeJson.Write(response);
            attempt.CompletedAt = store.Now;
            // New steering can arrive during the model call. Its output must not execute.
            t.Tokens += (response.Metadata?.Usage.TotalTokens ?? estimate) - estimate;
            if (t.Revision != revision + 1) return;
            t.Failures = 0;
            t.Reason = null;
            if (response.FunctionCalls.Count == 0)
            {
                if (s.Sources.Count == 0 && s.PendingChanges.Count == 0 && t.SavedChanges == 0
                    && !RequiresInitialMutation(t, s))
                    await CompleteAsync(t, s, response.Text, ct);
                else
                {
                    s.NoProgress++;
                    s.History.Add(RuntimeJson.Text("user", "Verify completion with finish_task, or continue remaining sections and unfinished drafts. A prose answer alone does not finish this task."));
                }
                return;
            }
            s.Calls = response.FunctionCalls.ToList();
            s.NextCall = 0;
            s.CallResults.Clear();
            s.History.Add(new("model", RuntimeJson.Write(new
            {
                parts = response.FunctionCalls.Select(c => new { kind = "function_call", name = c.Name, argsJson = c.ArgsJson, callId = c.CallId, thoughtSignature = c.ThoughtSignature }),
                outputItemsJson = response.OutputItemsJson,
            })));
        }, ct);
    }

    private async Task PrefetchAsync(AssistantTask task, AssistantRuntimeState state, Guid lease, CancellationToken ct)
    {
        if (prefetcher is null || state.PrefetchedReads.Count > 0) return;
        var runtime = await contexts.BuildAsync(task, state, ct);
        var candidates = state.Calls.Skip(state.NextCall).Take(3).TakeWhile(call =>
        {
            var canonical = tools.ResolveCanonicalName(call.Name);
            var declaration = runtime.Request.Tools.FirstOrDefault(x => x.Name == call.Name || x.Name == canonical);
            return canonical is not null && ToolExecutionPolicy.For(canonical).Operation == "read"
                && (runtime.Request.AllowedToolNames?.Contains(call.Name) == true || runtime.Request.AllowedToolNames?.Contains(canonical) == true)
                && declaration is not null && RuntimeToolSchema.Validate(SafeArguments(call.ArgsJson), JsonSerializer.SerializeToElement(declaration.ParametersJsonSchema)) is null;
        }).ToArray();
        if (candidates.Length < 2 || state.ResourceFingerprint != await FingerprintAsync(runtime.Tools.QuizId, ct)) return;
        await AtomicAsync(task.Id, lease, async (t, s) =>
        {
            if (s.SteeringCount != RuntimeJson.Read<List<string>>(t.SteeringJson).Count) return;
            for (var i = 0; i < candidates.Length; i++)
            {
                var sequence = s.CallSequence + i;
                if (await db.AssistantTaskCalls.AnyAsync(x => x.TaskId == t.Id && x.Sequence == sequence, ct)) continue;
                var call = candidates[i];
                var snapshot = new ToolDecisionSnapshot(RuntimeJson.Read<AssistantTaskInput>(t.RequestJson).Message,
                    RuntimeJson.Read<List<string>>(t.SteeringJson), new { runtime.Tools.QuizId, runtime.Tools.CurrentLanguage, runtime.Tools.IsFreestyle },
                    AgentToolFilter.Narrow(runtime.Request.Tools, runtime.Request.AllowedToolNames).Select(x => new { x.Name, x.Description, policy = ToolExecutionPolicy.For(x.Name) }).ToArray(),
                    tools.ResolveCanonicalName(call.Name)!, SafeArguments(call.ArgsJson),
                    (await db.AssistantTaskCalls.AsNoTracking().Where(x => x.TaskId == t.Id && x.ResultJson != null)
                        .OrderByDescending(x => x.Sequence).Take(6).ToListAsync(ct))
                        .Select(x => (object)new { x.Sequence, x.ToolName, x.Status, result = RuntimeJson.Read<object>(x.ResultJson!) }).ToArray());
                var json = RuntimeJson.Write(snapshot);
                db.AssistantTaskCalls.Add(new AssistantTaskCall { Id = Guid.NewGuid(), TaskId = t.Id, Sequence = sequence,
                    ToolName = snapshot.Tool, ArgumentsJson = call.ArgsJson, SnapshotJson = json, SnapshotHash = RuntimeJson.Hash(json),
                    CreatedAt = store.Now, EvaluationStatus = jev.Value.Enabled ? "pending" : "disabled" });
            }
        }, ct, release: false);
        var results = await prefetcher.ReadAsync(candidates, runtime.Tools, ct);
        await AtomicAsync(task.Id, lease, (t, s) =>
        {
            if (s.SteeringCount != RuntimeJson.Read<List<string>>(t.SteeringJson).Count) return Task.CompletedTask;
            for (var i = 0; i < results.Count; i++) s.PrefetchedReads[s.NextCall + i] = results[i];
            return Task.CompletedTask;
        }, ct, release: false);
    }

    private async Task ExecuteCallAsync(Guid id, Guid lease, CancellationToken ct)
    {
        (RuntimeContext Runtime, AssistantRuntimeState State, string JournalStatus)? plan = null;
        await AtomicAsync(id, lease, async (task, state) =>
        {
            plan = null;
            var call = state.Calls[state.NextCall];
            var runtime = await contexts.BuildAsync(task, state, ct);
            if (RuntimeJson.Read<List<string>>(task.SteeringJson).Count != state.SteeringCount) return;
            var canonical = tools.ResolveCanonicalName(call.Name) ?? call.Name;
            var sequence = state.CallSequence;
            var journal = await db.AssistantTaskCalls.SingleOrDefaultAsync(x => x.TaskId == id && x.Sequence == sequence, ct);
            if (journal is null)
            {
                var snapshot = new ToolDecisionSnapshot(RuntimeJson.Read<AssistantTaskInput>(task.RequestJson).Message,
                    RuntimeJson.Read<List<string>>(task.SteeringJson),
                    new { runtime.Tools.QuizId, runtime.Tools.FocusedWordId, runtime.Tools.IsFreestyle, runtime.Tools.CurrentLanguage, requestedContentKind = runtime.Tools.RequestedContentKind.ToString() },
                    AgentToolFilter.Narrow(runtime.Request.Tools, runtime.Request.AllowedToolNames).Select(x => new { x.Name, x.Description, policy = ToolExecutionPolicy.For(x.Name) }).ToArray(),
                    canonical, SafeArguments(call.ArgsJson),
                    (await db.AssistantTaskCalls.AsNoTracking().Where(x => x.TaskId == id && x.ResultJson != null)
                        .OrderByDescending(x => x.Sequence).Take(6).ToListAsync(ct))
                        .Select(x => (object)new { x.Sequence, x.ToolName, x.Status, result = RuntimeJson.Read<object>(x.ResultJson!) }).ToArray());
                var json = RuntimeJson.Write(snapshot);
                journal = new AssistantTaskCall
                {
                    Id = Guid.NewGuid(), TaskId = id, Sequence = sequence, ToolName = canonical,
                    ArgumentsJson = call.ArgsJson, SnapshotJson = json, SnapshotHash = RuntimeJson.Hash(json),
                    CreatedAt = store.Now, EvaluationStatus = jev.Value.Enabled ? "pending" : "disabled",
                };
                db.AssistantTaskCalls.Add(journal);
            }
            plan = (runtime, state, journal.Status);
        }, ct, release: false);
        var prepared = plan is { } planned ? await PrepareToolAsync(planned.Runtime, planned.State, planned.JournalStatus, ct) : null;
        await AtomicAsync(id, lease, async (task, state) =>
        {
            if (RuntimeJson.Read<List<string>>(task.SteeringJson).Count != state.SteeringCount) return;
            var call = state.Calls[state.NextCall];
            var runtime = await contexts.BuildAsync(task, state, ct);
            var canonical = tools.ResolveCanonicalName(call.Name) ?? call.Name;
            var sequence = state.CallSequence;
            var journal = await db.AssistantTaskCalls.SingleAsync(x => x.TaskId == id && x.Sequence == sequence, ct);
            AssistantToolOutcome outcome;
            var before = task.SavedChanges;
            try
            {
                if (runtime.Request.AllowedToolNames?.Contains(canonical) != true && runtime.Request.AllowedToolNames?.Contains(call.Name) != true)
                    outcome = new("correctable", Error: "This tool is unavailable for this request. Choose an offered tool.");
                else if (RuntimeToolSchema.Validate(ParseObject(call.ArgsJson), JsonSerializer.SerializeToElement(
                    runtime.Request.Tools.First(x => x.Name == call.Name || x.Name == canonical).ParametersJsonSchema)) is { } schemaError)
                    outcome = new("correctable", Error: schemaError);
                else if (RuntimeTools.Declarations.Any(x => x.Name == canonical))
                    outcome = await ControlAsync(task, state, call, ct);
                else if (state.ResourceFingerprint != await FingerprintAsync(runtime.Tools.QuizId, ct))
                {
                    state.ResourceFingerprint = await FingerprintAsync(runtime.Tools.QuizId, ct);
                    state.PendingChanges.RemoveAll(x => x.Kind != PendingChangeKinds.CreateQuiz);
                    task.ApprovalGranted = false;
                    // Not "correctable": the arguments were not at fault. After re-reading, the
                    // model may repeat this exact call or drop it to keep the user's edit.
                    outcome = new("conflict", Error: "Quiz content changed since planning. Read current content and replan; do not overwrite the user's edits.");
                }
                else if (journal.Status == "awaiting_approval" && task.ApprovalGranted)
                {
                    await SaveChangesAsync(task, state, runtime.Tools.QuizId, ct);
                    // Approval applies accepted proposals; it does not repair skipped items.
                    var proposed = RuntimeJson.Read<AssistantToolOutcome>(journal.ResultJson!);
                    var proposalData = JsonSerializer.SerializeToElement(proposed.Data, RuntimeJson.Options);
                    var partial = proposalData.ValueKind == JsonValueKind.Object && proposalData.EnumerateObject().Any(x =>
                        x.Name.StartsWith("skipped", StringComparison.Ordinal) && x.Value.ValueKind == JsonValueKind.Array && x.Value.GetArrayLength() > 0);
                    outcome = new(partial ? "partial" : "success", proposed.Data, Saved: task.SavedChanges - before);
                    task.ApprovalGranted = false;
                }
                else
                {
                    var identical = await db.AssistantTaskCalls.AsNoTracking().Where(x => x.TaskId == id && x.Sequence < sequence
                        && x.ToolName == canonical && x.ArgumentsJson == call.ArgsJson
                        && (x.Status == "correctable" || x.Status == "success")).FirstOrDefaultAsync(ct);
                    if (identical is not null && ToolExecutionPolicy.For(canonical).Operation != "read")
                        outcome = new("correctable", Error: "This identical call already ran. Inspect its result and change the arguments or continue with remaining work.");
                    else
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(TimeSpan.FromSeconds(ToolExecutionPolicy.For(canonical).TimeoutSeconds));
                        var cacheable = Cacheable(canonical);
                        var cacheKey = ReadCacheKey(canonical, call, state);
                        // The fingerprint check above proves the quiz still matches the plan the
                        // prepared result was computed against; any other drift discards it.
                        var usePrepared = prepared is not null && prepared.Sequence == sequence
                            && prepared.SteeringCount == state.SteeringCount && prepared.PendingHash == PendingHash(state);
                        var raw = state.PrefetchedReads.TryGetValue(state.NextCall, out var prefetched)
                            ? RuntimeJson.Read<object>(prefetched)
                            : usePrepared
                                ? RuntimeJson.Read<object>(prepared!.ResultJson)
                                : cacheable && state.ReadCache.TryGetValue(cacheKey, out var cached)
                                    ? RuntimeJson.Read<object>(cached)
                                    : await tools.ExecuteAsync(call.Name, call.ArgsJson, runtime.Tools, timeout.Token);
                        if (cacheable)
                        {
                            if (state.ReadCache.Count >= 6) state.ReadCache.Remove(state.ReadCache.Keys.First());
                            state.ReadCache[cacheKey] = RuntimeJson.Write(raw);
                        }
                        var element = JsonSerializer.SerializeToElement(raw, RuntimeJson.Options);
                        state.PendingChanges = usePrepared ? prepared!.PendingChanges : runtime.Tools.PendingChanges;
                        if (element.TryGetProperty("error", out var error)) outcome = new("correctable", raw, error.GetString());
                        else if (!task.ManualApproval && ToolExecutionPolicy.NeedsApproval(Unapplied(state)))
                        {
                            task.Status = "awaiting_approval";
                            task.Reason = "Review the proposed changes before applying them.";
                            outcome = new("awaiting_approval", raw);
                        }
                        else
                        {
                            if (!task.ManualApproval) await SaveChangesAsync(task, state, runtime.Tools.QuizId, ct);
                            var partial = element.EnumerateObject().Any(x => x.Name.StartsWith("skipped", StringComparison.Ordinal)
                                && x.Value.ValueKind == JsonValueKind.Array && x.Value.GetArrayLength() > 0);
                            outcome = new(partial ? "partial" : task.ManualApproval ? "proposed" : "success", raw, Saved: task.SavedChanges - before);
                        }
                    }
                }
            }
            catch (JsonException) { outcome = new("correctable", Error: "Arguments must be a valid JSON object. Correct the call."); }
            // Other exceptions escape and roll back both the content and journal transaction.
            journal.Status = outcome.Status;
            journal.ResultJson = RuntimeJson.Write(outcome);
            if (outcome.Status == "awaiting_approval") return;
            if (outcome.Saved > 0) state.ReadCache.Clear();
            state.CallResults.Add(ResponsePart(call, new { sequence, outcome }));
            state.NextCall++;
            state.CallSequence++;
            RuntimeMutationCorrections.Observe(state, canonical, call.ArgsJson, outcome, sequence);
            var earlierResults = outcome.Saved == 0 ? await db.AssistantTaskCalls.AsNoTracking()
                .Where(x => x.TaskId == id && x.Sequence < sequence && x.ToolName == canonical && x.ResultJson != null)
                .OrderByDescending(x => x.Sequence).Take(6).Select(x => x.ResultJson!).ToListAsync(ct) : [];
            var currentResult = RuntimeJson.Read<JsonElement>(journal.ResultJson);
            var repeatedResult = earlierResults.Any(x => JsonElement.DeepEquals(RuntimeJson.Read<JsonElement>(x), currentResult));
            state.NoProgress = outcome.Status is "correctable" or "partial" or "conflict" || repeatedResult ? state.NoProgress + 1 : 0;
            if (outcome.Error is not null) state.LastErrors.Add(outcome.Error);
            if (state.LastErrors.Count > 10) state.LastErrors.RemoveRange(0, state.LastErrors.Count - 10);
            state.ResourceFingerprint = await FingerprintAsync(runtime.Tools.QuizId, ct);
            // The rest of the batch was planned against the old content. With the fingerprint
            // refreshed it would pass the check above, so it must be replanned, not executed.
            if (outcome.Status == "conflict") await SupersedeRemainingAsync(id, state, "Quiz content changed since planning. Replan from current content.", ct);
            else if (state.NextCall == state.Calls.Count) FlushResults(state);
        }, ct, isolation: IsolationLevel.Serializable);
    }

    /// <summary>Closes the batch's unexecuted calls so provider history stays paired, then flushes it.</summary>
    private async Task SupersedeRemainingAsync(Guid id, AssistantRuntimeState state, string reason, CancellationToken ct)
    {
        for (; state.NextCall < state.Calls.Count; state.NextCall++)
        {
            var outcome = new AssistantToolOutcome("superseded", Error: reason);
            state.CallResults.Add(ResponsePart(state.Calls[state.NextCall], outcome));
            var journal = await db.AssistantTaskCalls.SingleOrDefaultAsync(x => x.TaskId == id && x.Sequence == state.CallSequence, ct);
            if (journal is not null) { journal.Status = "superseded"; journal.ResultJson = RuntimeJson.Write(outcome); }
            state.CallSequence++;
        }
        FlushResults(state);
    }

    private sealed record PreparedTool(int Sequence, int SteeringCount, string PendingHash, string ResultJson,
        List<PendingChange> PendingChanges);

    /// <summary>
    /// Runs a data tool before the content transaction. Tools only read and build proposals,
    /// so the Serializable transaction then holds range locks only for the recheck and save,
    /// not for the tool's reads.
    /// </summary>
    private async Task<PreparedTool?> PrepareToolAsync(RuntimeContext runtime, AssistantRuntimeState state,
        string journalStatus, CancellationToken ct)
    {
        var call = state.Calls[state.NextCall];
        var canonical = tools.ResolveCanonicalName(call.Name);
        // Anything the transaction would not execute stays there: control tools, approvals,
        // prefetched or cached reads, and calls that fail the offer or schema checks.
        if (canonical is null || RuntimeTools.Declarations.Any(x => x.Name == canonical) || journalStatus == "awaiting_approval"
            || state.PrefetchedReads.ContainsKey(state.NextCall)
            || runtime.Request.AllowedToolNames?.Contains(canonical) != true && runtime.Request.AllowedToolNames?.Contains(call.Name) != true
            || Cacheable(canonical) && state.ReadCache.ContainsKey(ReadCacheKey(canonical, call, state)))
            return null;
        var declaration = runtime.Request.Tools.FirstOrDefault(x => x.Name == call.Name || x.Name == canonical);
        try
        {
            if (declaration is null || RuntimeToolSchema.Validate(ParseObject(call.ArgsJson),
                JsonSerializer.SerializeToElement(declaration.ParametersJsonSchema)) is not null) return null;
        }
        catch (JsonException) { return null; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(ToolExecutionPolicy.For(canonical).TimeoutSeconds));
        db.ChangeTracker.Clear();
        var raw = await tools.ExecuteAsync(call.Name, call.ArgsJson, runtime.Tools, timeout.Token);
        return new(state.CallSequence, state.SteeringCount, PendingHash(state), RuntimeJson.Write(raw), [.. runtime.Tools.PendingChanges]);
    }

    private static bool Cacheable(string canonical) => canonical is "get_word" or "list_words" or "list_sentences" or "get_quiz_summary";
    private static string ReadCacheKey(string canonical, AgentFunctionCall call, AssistantRuntimeState state) =>
        RuntimeJson.Hash(canonical + call.ArgsJson + state.ResourceFingerprint);
    private static string PendingHash(AssistantRuntimeState state) => RuntimeJson.Hash(RuntimeJson.Write(state.PendingChanges));

    private async Task<AssistantToolOutcome> ControlAsync(AssistantTask task, AssistantRuntimeState state, AgentFunctionCall call, CancellationToken ct)
    {
        var args = ParseObject(call.ArgsJson);
        string Value(string key) => args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        switch (call.Name)
        {
            case "read_source_section":
                var section = state.Sources.SingleOrDefault(x => x.Id == Value("section_id"));
                return section is null ? new("correctable", Error: "Unknown source section.") : new("success", section);
            case "complete_source_section":
                var sectionId = Value("section_id");
                if (!state.Sources.Any(x => x.Id == sectionId)) return new("correctable", Error: "Unknown source section.");
                var source = state.Sources.Single(x => x.Id == sectionId);
                var evidence = JsonSerializer.Deserialize<List<CoverageSpan>>(Value("coverage_json"), RuntimeJson.Options);
                if (evidence is null || evidence.Count == 0) return new("correctable", Error: "Supply coverage spans.");
                var savedCalls = await db.AssistantTaskCalls.AsNoTracking().Where(x => x.TaskId == task.Id && x.ResultJson != null).ToListAsync(ct);
                var savedSequences = savedCalls.Where(x => RuntimeJson.Read<AssistantToolOutcome>(x.ResultJson!).Saved > 0
                    || task.ManualApproval && x.Status == "proposed").Select(x => x.Sequence).ToHashSet();
                var covered = new bool[source.Text.Length];
                foreach (var span in evidence)
                {
                    if (span.Start < 0 || span.Length <= 0 || span.Start > covered.Length - span.Length
                        || !(span.SavedCall.HasValue && savedSequences.Contains(span.SavedCall.Value))
                            && string.IsNullOrWhiteSpace(span.ExclusionReason))
                        return new("correctable", Error: "Each valid span must reference a saved call or an explicit exclusion.");
                    Array.Fill(covered, true, span.Start, span.Length);
                }
                if (source.Text.Where((character, index) => !char.IsWhiteSpace(character) && !covered[index]).Any())
                    return new("correctable", Error: "Coverage leaves source text unprocessed. Supply the missing spans.");
                if (!state.CoveredSections.Contains(sectionId)) state.CoveredSections.Add(sectionId);
                state.CoverageEvidence[sectionId] = Value("coverage_json");
                return new("success", new { sectionId });
            case "resolve_rejected_item":
                var key = Value("mutation_key");
                if (!key.Contains(":unidentified:", StringComparison.Ordinal)
                    || !state.UnresolvedMutations.TryGetValue(key, out var rejectedSequence)
                    || !int.TryParse(Value("saved_call"), out var correctedSequence)
                    || correctedSequence <= rejectedSequence || state.MutationCorrectionEvidence.Contains(correctedSequence))
                    return new("correctable", Error: "Link one unidentified rejected item to a later, unused successful correction call.");
                var correction = await db.AssistantTaskCalls.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.TaskId == task.Id && x.Sequence == correctedSequence, ct);
                var rejectedTool = key.Split(':')[1];
                if (correction?.ResultJson is null
                    || RuntimeMutationCorrections.Family(correction.ToolName) != RuntimeMutationCorrections.Family(rejectedTool)
                    || !(correction.Status == "success" && RuntimeJson.Read<AssistantToolOutcome>(correction.ResultJson).Saved > 0
                        || task.ManualApproval && correction.Status == "proposed"))
                    return new("correctable", Error: "The evidence must be a successful correction of the same mutation type, not a read, failure, or partial batch.");
                var rejectedCall = await db.AssistantTaskCalls.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.TaskId == task.Id && x.Sequence == rejectedSequence, ct);
                if (rejectedCall is null || !RuntimeMutationCorrections.MatchesCorrection(key, rejectedTool,
                    rejectedCall.ArgumentsJson, correction.ToolName, correction.ArgumentsJson))
                    return new("correctable", Error: "The correction must contain exactly one matching item and preserve the rejected item's usable text and translation. If no usable content remains, ask the user to clarify.");
                state.MutationCorrectionEvidence.Add(correctedSequence);
                state.UnresolvedMutations.Remove(key);
                state.NeedsCorrection = state.UnresolvedMutations.Count > 0;
                return new("success", new { resolved = key, correctedSequence });
            case "ask_user":
                if (task.ManualApproval)
                {
                    // A synchronous caller has no reply or Resume path into this task. As in
                    // the legacy assistant, the question ends the turn; the answer starts a new
                    // task that replays this question from the chat.
                    await CompleteAsync(task, state, Value("question"), ct);
                    return new("blocked", Error: Value("question"));
                }
                task.Status = "awaiting_input";
                task.Reason = Value("question");
                await AddMessageAsync(task, state, "model", task.Reason, null, ct);
                return new("blocked", Error: task.Reason);
            case "finish_task":
                if (state.NextCall != state.Calls.Count - 1) return new("correctable", Error: "Finish must be the last call; complete other work first.");
                if (state.NeedsCorrection) return new("correctable", Error: "A rejected or partial mutation remains unresolved. Correct the affected items before finishing.");
                if (task.SavedChanges == 0 && state.PendingChanges.Count == 0
                    && RequiresInitialMutation(task, state))
                    return new("correctable", Error: "The requested creation or addition has not been saved or proposed. Perform it before finishing.");
                if (state.Sources.Any(x => !state.CoveredSections.Contains(x.Id)))
                    return new("correctable", Error: "Source sections remain uncovered. Read and process every remaining section.");
                if (state.PendingChanges.Any(x => x.Kind == PendingChangeKinds.CreateQuiz
                    && x.Payload.TryGetProperty("complete", out var complete) && complete.ValueKind == JsonValueKind.False))
                    return new("correctable", Error: "A quiz draft is incomplete. Finish remaining batches first.");
                if (!task.ManualApproval && state.DraftCollections.Count > 0)
                {
                    foreach (var (draft, collection) in state.DraftCollections)
                    {
                        var quizId = state.DraftQuizzes[draft];
                        if (state.PendingChanges.Any(x => x.Kind == PendingChangeKinds.MoveQuiz
                            && x.Payload.GetProperty("quiz_id").GetGuid() == quizId)) continue;
                        var quizName = await db.Quizzes.Where(x => x.Id == quizId && x.UserId == task.UserId).Select(x => x.Name).SingleAsync(ct);
                        var collectionName = await db.Collections.Where(x => x.Id == collection && x.UserId == task.UserId).Select(x => x.Name).SingleAsync(ct);
                        var move = new PendingChange(PendingChangeKinds.MoveQuiz, JsonSerializer.SerializeToElement(new
                        { quiz_id = quizId, quiz_name = quizName, collection_id = collection, collection_name = collectionName }));
                        state.PendingChanges.Add(move);
                    }
                    if (!task.ApprovalGranted)
                    {
                        task.Status = "awaiting_approval";
                        task.Reason = "The quiz is built privately. Review moving it into the requested collection, which may make it public.";
                        return new("awaiting_approval");
                    }
                    foreach (var move in Unapplied(state).Where(x => x.Kind == PendingChangeKinds.MoveQuiz))
                    {
                        var applied = await applier.ApplyAsync(null, task.UserId, [move], ct);
                        if (applied.Applied == 0) throw new InvalidOperationException("The destination collection could not be applied.");
                        task.SavedChanges += applied.Applied;
                        state.AppliedChanges.Add(RuntimeJson.Hash(RuntimeJson.Write(move)));
                    }
                    state.DraftCollections.Clear();
                    task.ApprovalGranted = false;
                }
                if (await db.AssistantTaskCalls.AnyAsync(x => x.TaskId == task.Id && x.Sequence != state.CallSequence && x.Status == "awaiting_approval", ct))
                    return new("correctable", Error: "Changes still await approval.");
                await CompleteAsync(task, state, Value("summary"), ct);
                return new("success", new { savedChanges = task.SavedChanges });
            default: return new("correctable", Error: "Unknown control tool.");
        }
    }

    private sealed record CoverageSpan(int Start, int Length,
        [property: System.Text.Json.Serialization.JsonPropertyName("saved_call")] int? SavedCall,
        [property: System.Text.Json.Serialization.JsonPropertyName("exclusion_reason")] string? ExclusionReason);

    private static bool RequiresInitialMutation(AssistantTask task, AssistantRuntimeState state) =>
        // Later steering can withdraw an earlier creation/addition request. Do not force
        // a write using a keyword from the superseded initial instruction.
        state.SteeringCount == 0 && AssistantMutationRequest.IsExplicitInitialCommand(
            RuntimeJson.Read<AssistantTaskInput>(task.RequestJson).Message);

    private static List<PendingChange> Unapplied(AssistantRuntimeState state) => state.PendingChanges
        .Where(x => x.Kind == PendingChangeKinds.CreateQuiz
            ? !state.DraftQuizzes.ContainsKey(x.Payload.GetProperty("draft_id").GetString()!)
            : !state.AppliedChanges.Contains(RuntimeJson.Hash(RuntimeJson.Write(x)))).ToList();

    private async Task SaveChangesAsync(AssistantTask task, AssistantRuntimeState state, Guid? quizId, CancellationToken ct)
    {
        foreach (var change in state.PendingChanges.ToArray())
        {
            var key = RuntimeJson.Hash(RuntimeJson.Write(change));
            if (change.Kind != PendingChangeKinds.CreateQuiz)
            {
                if (!state.AppliedChanges.Add(key)) continue;
                var applied = await applier.ApplyAsync(quizId, task.UserId, [change], ct);
                task.SavedChanges += applied.Applied;
                continue;
            }
            var draft = change.Payload.GetProperty("draft_id").GetString()!;
            if (!state.DraftQuizzes.TryGetValue(draft, out var createdId))
            {
                var privatePayload = JsonNode.Parse(change.Payload.GetRawText())!.AsObject();
                if (privatePayload["collection_id"] is JsonValue destination && Guid.TryParse(destination.ToString(), out var collectionId))
                {
                    if (!await db.Collections.AnyAsync(x => x.Id == collectionId && x.UserId == task.UserId, ct))
                        throw new InvalidOperationException("The requested destination collection was not found.");
                    state.DraftCollections[draft] = collectionId;
                }
                privatePayload["collection_id"] = null;
                var privateChange = change with { Payload = JsonSerializer.SerializeToElement(privatePayload) };
                var applied = await applier.ApplyAsync(null, task.UserId, [privateChange], ct);
                if (applied.CreatedQuizId is not Guid newId) throw new InvalidOperationException("Quiz creation produced no saved artifact.");
                createdId = newId;
                state.DraftQuizzes[draft] = createdId;
                // The applier can drop items (duplicates, words that repeat a sentence). Count
                // and record only what the new quiz actually holds.
                var stored = await StoredItemKeysAsync(createdId, ct);
                state.SavedDraftItems[draft] = DraftItems(change).Select(ItemKey).Where(stored.Keys.Contains).Distinct().ToList();
                task.SavedChanges += applied.Applied + stored.Rows;
            }
            else
            {
                var saved = state.SavedDraftItems[draft];
                var additions = DraftItems(change).Where(x => !saved.Contains(ItemKey(x))).ToList();
                if (additions.Count > 0)
                {
                    var applied = await applier.ApplyAsync(createdId, task.UserId, additions, ct);
                    task.SavedChanges += applied.Applied;
                    var stored = await StoredItemKeysAsync(createdId, ct);
                    saved.AddRange(additions.Select(ItemKey).Where(stored.Keys.Contains).Distinct().Where(x => !saved.Contains(x)).ToList());
                }
            }
            var quiz = await db.Quizzes.SingleAsync(x => x.Id == createdId && x.UserId == task.UserId, ct);
            // Private until the entire task completes, even when created inside a public collection.
            quiz.IsPublic = false;
            quiz.ProcessingStatus = "Building";
            quiz.ProcessingMessage = "The assistant is saving this quiz in batches.";
        }
    }

    private static IEnumerable<PendingChange> DraftItems(PendingChange change)
    {
        foreach (var word in change.Payload.GetProperty("words").EnumerateArray()) yield return new(PendingChangeKinds.AddWord, word);
        foreach (var sentence in change.Payload.GetProperty("sentences").EnumerateArray()) yield return new(PendingChangeKinds.AddSentence, sentence);
    }
    private static string ItemKey(PendingChange item) => ItemKey(item.Kind,
        item.Payload.GetProperty(item.Kind == PendingChangeKinds.AddWord ? "word" : "text").GetString());
    private static string ItemKey(string kind, string? text) =>
        kind + ":" + Tools.ToolArguments.NormalizeForDuplicateMatch(text).ToUpperInvariant();

    private async Task<(HashSet<string> Keys, int Rows)> StoredItemKeysAsync(Guid quizId, CancellationToken ct)
    {
        var words = await db.Words.AsNoTracking().Where(x => x.QuizId == quizId).Select(x => x.Lemma).ToListAsync(ct);
        var sentences = await db.QuizSentences.AsNoTracking().Where(x => x.QuizId == quizId).Select(x => x.Text).ToListAsync(ct);
        var keys = words.Select(x => ItemKey(PendingChangeKinds.AddWord, x))
            .Concat(sentences.Select(x => ItemKey(PendingChangeKinds.AddSentence, x))).ToHashSet();
        return (keys, words.Count + sentences.Count);
    }

    private async Task CompleteAsync(AssistantTask task, AssistantRuntimeState state, string text, CancellationToken ct,
        string? failureReason = null)
    {
        task.Status = failureReason is null ? "completed" : "failed";
        task.ActiveUserId = null;
        task.Reason = failureReason;
        state.FinalText = string.IsNullOrWhiteSpace(text) ? $"Finished. Saved {task.SavedChanges} changes." : text;
        var message = await AddMessageAsync(task, state, "model", state.FinalText,
            task.ManualApproval ? state.PendingChanges : null, ct);
        state.FinalMessageId = message.Id;
        var response = new AssistantTurnResponse(task.ThreadId, state.TurnId, message.Id, state.FinalText, [],
            task.ManualApproval ? state.PendingChanges.Select(x => presenter.PresentPendingChange(x, new Dictionary<string, AssistantWordLabel>())).ToList() : [],
            task.ManualApproval ? AssistantMessageStatus.Active : AssistantMessageStatus.Applied);
        task.ResultJson = RuntimeJson.Write(response);
        var turn = await db.AssistantTurns.SingleAsync(x => x.Id == state.TurnId, ct);
        turn.Status = AssistantTurnStatus.Completed;
        turn.CompletedAt = new DateTimeOffset(store.Now, TimeSpan.Zero);
        turn.FinalMessageId = message.Id;
        turn.ErrorCategory = failureReason is null ? null : "task_stopped";
        await SetQuizStatusAsync(task, state, failureReason is null ? "Ready" : "Incomplete", ct);
    }
    private async Task PauseAsync(AssistantTask task, AssistantRuntimeState state, string reason, CancellationToken ct)
    {
        if (task.ManualApproval)
        {
            // Synchronous callers cannot resume, steer, or stop a task, so a paused one would
            // hold the user's only active slot indefinitely. End it like a capped legacy turn:
            // say why, keep any proposals for Apply, and release the slot.
            var cause = reason.Split(". ", 2)[0].TrimEnd('.');
            cause = char.ToLowerInvariant(cause[0]) + cause[1..];
            await CompleteAsync(task, state,
                $"I could not finish this request: {cause}. Any proposed changes are partial; the full request is not complete.",
                ct, reason);
            return;
        }
        task.Status = "paused";
        task.Reason = reason;
        await SetQuizStatusAsync(task, state, "Paused", ct);
    }
    private async Task SetQuizStatusAsync(AssistantTask task, AssistantRuntimeState state, string status, CancellationToken ct)
    {
        var ids = state.DraftQuizzes.Values.ToArray();
        foreach (var quiz in await db.Quizzes.Where(x => ids.Contains(x.Id) && x.UserId == task.UserId).ToListAsync(ct))
        { quiz.ProcessingStatus = status; quiz.ProcessingMessage = status == "Ready" ? null : task.Reason; }
    }
    private async Task<AssistantMessage> AddMessageAsync(AssistantTask task, AssistantRuntimeState state, string role, string text,
        IReadOnlyList<PendingChange>? pending, CancellationToken ct)
    {
        var sequence = (await db.AssistantMessages.Where(x => x.ThreadId == task.ThreadId).Select(x => (int?)x.Sequence).MaxAsync(ct) ?? -1) + 1;
        sequence += db.ChangeTracker.Entries<AssistantMessage>().Count(x => x.State == EntityState.Added && x.Entity.ThreadId == task.ThreadId);
        var message = new AssistantMessage
        {
            Id = Guid.NewGuid(), ThreadId = task.ThreadId, TurnId = state.TurnId, Sequence = sequence,
            ContextQuizId = RuntimeJson.Read<AssistantTaskInput>(task.RequestJson).ContextQuizId,
            Role = role, ContentJson = RuntimeJson.Text(role, text).ContentJson,
            PendingChangesJson = pending is { Count: > 0 } ? RuntimeJson.Write(pending) : null,
            Status = AssistantMessageStatus.Active, CreatedAt = new DateTimeOffset(store.Now, TimeSpan.Zero),
        };
        db.AssistantMessages.Add(message);
        var thread = await db.AssistantThreads.SingleAsync(x => x.Id == task.ThreadId, ct);
        thread.UpdatedAt = message.CreatedAt;
        return message;
    }

    private async Task<string> FingerprintAsync(Guid? id, CancellationToken ct)
    {
        if (id is null) return "none";
        var words = await db.Words.AsNoTracking().Where(x => x.QuizId == id).OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.Lemma, x.Translation }).ToListAsync(ct);
        var sentences = await db.QuizSentences.AsNoTracking().Where(x => x.QuizId == id).OrderBy(x => x.Id).ToListAsync(ct);
        return RuntimeJson.Hash(RuntimeJson.Write(new { words, sentences }));
    }
    private Task<AssistantTask?> LoadAsync(Guid id, Guid lease, CancellationToken ct) => db.AssistantTasks
        .SingleOrDefaultAsync(x => x.Id == id && x.LeaseId == lease && x.LeaseUntil > store.Now && x.Status == "running", ct);

    private async Task AtomicAsync(Guid id, Guid lease, Func<AssistantTask, AssistantRuntimeState, Task> action,
        CancellationToken ct, bool release = true, IsolationLevel isolation = IsolationLevel.ReadCommitted)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            // Checkpoints need only the task row's lock. Serializable is reserved for the
            // content save, where it keeps a user's edit from landing between the quiz
            // recheck and the write; it would otherwise block that user's quiz edits.
            await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(isolation, ct) : null;
            var task = await LoadAsync(id, lease, ct);
            if (task is null) return;
            // Acquire the task row for the transaction. A changed revision or lost lease
            // prevents content commits; steering/cancel cannot race past this boundary.
            task.Revision++;
            await db.SaveChangesAsync(ct);
            var state = RuntimeJson.Read<AssistantRuntimeState>(task.StateJson);
            var previousStatus = task.Status;
            var previousSaved = task.SavedChanges;
            await action(task, state);
            task.StateJson = RuntimeJson.Write(state);
            task.UpdatedAt = store.Now;
            if (release) { task.LeaseId = null; task.LeaseUntil = store.Now; }
            await db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
            if (task.SavedChanges > previousSaved) AssistantRuntimeTelemetry.SavedChanges.Add(task.SavedChanges - previousSaved);
            if (task.Status != previousStatus) AssistantRuntimeTelemetry.Tasks.Add(1, new KeyValuePair<string, object?>("status", task.Status));
            if (task.Status == "retry_wait") AssistantRuntimeTelemetry.Retries.Add(1);
        });
    }
    private static JsonElement SafeArguments(string json)
    {
        try { return ParseObject(json); }
        catch (JsonException) { return JsonSerializer.SerializeToElement(new { malformedArguments = json }); }
    }
    private static JsonElement ParseObject(string json)
    {
        var value = JsonSerializer.Deserialize<JsonElement>(json);
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Expected an object.");
        return value;
    }
    private static object ResponsePart(AgentFunctionCall call, object result) => new
    { kind = "function_response", name = call.Name, callId = call.CallId, responseJson = RuntimeJson.Write(result) };
    private static void FlushResults(AssistantRuntimeState state)
    {
        if (state.CallResults.Count > 0) state.History.Add(new("user", RuntimeJson.Write(new { parts = state.CallResults })));
        state.Calls.Clear(); state.CallResults.Clear(); state.PrefetchedReads.Clear(); state.NextCall = 0;
    }
    internal static void Compact(AssistantRuntimeState state)
    {
        // Drop earlier chat text first, then complete exchanges after this task's request:
        // a model turn with the results and notes that follow it. The request and the most
        // recent exchange, including encrypted reasoning items, always remain.
        var history = state.History;
        state.RequestIndex = Math.Clamp(state.RequestIndex, 0, Math.Max(0, history.Count - 1));
        while (history.Count > 9 && history.Sum(x => x.ContentJson.Length) > 24000)
        {
            if (state.RequestIndex > 0)
            {
                history.RemoveAt(0);
                state.RequestIndex--;
                continue;
            }
            var end = 2;
            while (end < history.Count && history[end].Role != "model") end++;
            if (end >= history.Count) break;
            history.RemoveRange(1, end - 1);
        }
    }
    private static TimeSpan RetryDelay(Exception ex, int failures)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
            if (current is OpenAiTransportException { RetryAfter: { } delay } && delay > TimeSpan.Zero)
                return delay + TimeSpan.FromMilliseconds(Random.Shared.Next(500));
        return TimeSpan.FromSeconds(Math.Pow(2, failures) + Random.Shared.NextDouble());
    }
    private static bool Transient(Exception ex) => ex is HttpRequestException or GenerativeAiTimeoutException
        or GenerativeAiDependencyUnavailableException or GenerativeAiUpstreamException;
}
