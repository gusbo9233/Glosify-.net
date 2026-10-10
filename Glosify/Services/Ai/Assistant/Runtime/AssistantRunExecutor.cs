using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Abuse;
using Glosify.Services.Ai.Assistant.Tools;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Quizzes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Ai.Assistant.Runtime;

/// <summary>
/// Advances a run by one step: one model call, one batch of reads, one saved change, or the
/// finish. Every step ends in a checkpoint, so a restarted worker picks up exactly where the
/// last one stopped.
/// </summary>
/// <remarks>
/// The loop is driven by stored state rather than by an in-memory call stack: the phase says
/// what comes next, unsettled tool parts are the queue, and the conversation is rebuilt from
/// messages and parts for every model call. Checkpoints are fenced by the lease and by the run
/// revision, so a lost lease, Stop, or steering message discards a step instead of racing it.
/// </remarks>
internal sealed class AssistantRunExecutor(
    GlosifyContext db,
    AssistantRunStore store,
    AssistantRunContext contexts,
    AssistantConversation conversation,
    AssistantToolbox toolbox,
    IServiceScopeFactory scopes,
    IGenerativeAiClient model,
    IChangeApplier applier,
    AssistantAnalyticsStore analytics,
    AssistantIntentResolver intents,
    AssistantRunSignals signals,
    IOptions<AssistantRuntimeOptions> options,
    IOptions<JevOptions> jev,
    ILogger<AssistantRunExecutor> logger)
{
    private const int RepeatLimit = 2;
    private const int NoProgressPause = 6;
    private const int MaxReminders = 2;
    private const int ReadBatch = 4;
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(30);

    public async Task StepAsync(Guid runId, Guid lease, CancellationToken cancellationToken)
    {
        try
        {
            await StepCoreAsync(runId, lease, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A command committed between this step's read and its checkpoint, so the checkpoint
            // rolled back. Hand the run back now rather than when the lease expires.
            db.ChangeTracker.Clear();
            var now = store.Now;
            await db.AssistantRuns.Where(run => run.Id == runId && run.LeaseId == lease)
                .ExecuteUpdateAsync(set => set.SetProperty(run => run.LeaseId, (Guid?)null).SetProperty(run => run.LeaseUntil, now), cancellationToken);
            signals.Notify(runId);
        }
    }

    private async Task StepCoreAsync(Guid runId, Guid lease, CancellationToken cancellationToken)
    {
        var run = await LoadAsync(runId, lease, cancellationToken);
        if (run is null)
        {
            return;
        }

        var state = RunJson.Read<AssistantRunState>(run.StateJson);
        try
        {
            if (!state.Initialized)
            {
                await InitializeAsync(run, lease, cancellationToken);
            }
            else if (state.PendingSteering.Count > 0)
            {
                await ApplySteeringAsync(run, lease, cancellationToken);
            }
            else if (state.Phase == RunPhases.Tools)
            {
                await ExecuteToolsAsync(run, state, lease, cancellationToken);
            }
            else if (state.Phase == RunPhases.Finish)
            {
                await FinishAsync(run, state, lease, cancellationToken);
            }
            else
            {
                await CallModelAsync(run, state, lease, cancellationToken);
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (ex is not (InsufficientAiCreditsException or MonthlyAiBudgetExceededException or ResourceQuotaException))
            {
                logger.LogWarning(ex, "Assistant run {RunId} step failed", runId);
            }

            db.ChangeTracker.Clear();
            await AtomicAsync(run, lease, (current, currentState) => HandleFailureAsync(current, currentState, ex, cancellationToken), cancellationToken);
        }
    }

    private async Task InitializeAsync(AssistantRun run, Guid lease, CancellationToken cancellationToken)
    {
        var input = RunJson.Read<AssistantRunInput>(run.RequestJson);
        var thread = await db.AssistantThreads.AsNoTracking().SingleAsync(candidate => candidate.Id == run.ThreadId, cancellationToken);
        AssistantRunContext.Resolved resolved;
        try
        {
            resolved = await contexts.ResolveAsync(run, input, thread, cancellationToken);
        }
        catch (Exception ex) when (ex is QuizNotFoundException or InvalidOperationException)
        {
            await AtomicAsync(run, lease, async (current, state) =>
            {
                current.CurrentMessageId ??= (await AddModelMessageAsync(current, cancellationToken)).Id;
                await FailAsync(current, state, ex.Message, ex.Message, "invalid_context", cancellationToken);
            }, cancellationToken);
            return;
        }

        await AtomicAsync(run, lease, async (current, state) =>
        {
            var now = store.Now;
            var stored = await db.AssistantThreads.SingleAsync(candidate => candidate.Id == current.ThreadId, cancellationToken);
            stored.ConversationLanguage ??= resolved.Facts.ReplyLanguage;
            stored.ContextQuizId = resolved.Facts.QuizId;
            stored.ContextTranscriptId = resolved.TranscriptId;
            stored.ContextBookDocumentId = resolved.BookDocumentId;
            db.AssistantParts.Add(new AssistantPart
            {
                Id = Guid.NewGuid(),
                MessageId = current.UserMessageId,
                RunId = current.Id,
                Sequence = await store.NextPartSequenceAsync(current.UserMessageId, cancellationToken),
                Type = AssistantPartTypes.Context,
                Text = resolved.ContextNote,
                CreatedAt = now,
            });
            current.CurrentMessageId = (await AddModelMessageAsync(current, cancellationToken)).Id;
            var intent = intents.Resolve(input.Message);
            var turn = await db.AssistantTurns.SingleAsync(candidate => candidate.Id == current.TurnId, cancellationToken);
            turn.Profile = resolved.Facts.Profile.ToString();
            turn.Provider = AiUsageProviders.OpenAi;
            turn.IntentArtifact = intent.ArtifactKind.ToString();
            turn.IntentContent = intent.ContentKind.ToString();
            turn.IntentOperation = intent.OperationKind.ToString();
            turn.TargetLanguage = Clip(resolved.Facts.TargetLanguage, 64);
            turn.SourceLanguage = Clip(resolved.Facts.SourceLanguage, 64);
            turn.ReplyLanguage = Clip(resolved.Facts.ReplyLanguage, 64);
            state.Facts = resolved.Facts;
            state.Initialized = true;
            state.Phase = RunPhases.Model;
            current.WindowStartedAt = now;
        }, cancellationToken);
    }

    private async Task ApplySteeringAsync(AssistantRun run, Guid lease, CancellationToken cancellationToken) =>
        await AtomicAsync(run, lease, async (current, state) =>
        {
            var now = store.Now;
            foreach (var part in await OpenToolPartsAsync(current, cancellationToken))
            {
                part.State = AssistantToolStates.Superseded;
                part.Output = RunJson.Write(new { note = "Not run: the user sent a new message first." });
                part.CompletedAt = now;
            }

            var steeringMessage = state.PendingSteering[^1];
            var latest = await db.AssistantParts.AsNoTracking()
                .Where(part => part.MessageId == steeringMessage && part.Type == AssistantPartTypes.Text)
                .Select(part => part.Text)
                .FirstOrDefaultAsync(cancellationToken);
            if (state.Facts is not null && latest is not null)
            {
                state.Facts = contexts.Steer(state.Facts, latest);
            }

            // Later parts belong after the steering message, so the reply continues in a new message.
            current.CurrentMessageId = (await AddModelMessageAsync(current, cancellationToken)).Id;
            state.PendingSteering.Clear();
            state.Phase = RunPhases.Model;
            state.NoProgress = 0;
            state.Reminders = 0;
            state.FinishingPlacements = false;
        }, cancellationToken);

    private async Task CallModelAsync(AssistantRun run, AssistantRunState state, Guid lease, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var timeExpired = store.Now >= run.WindowStartedAt.AddSeconds(settings.WindowSeconds);
        if ((timeExpired || run.Steps + 1 >= settings.MaxModelCalls)
            && run.SavedChanges > state.WindowSavedChanges && state.NoProgress < 3)
        {
            await AtomicAsync(run, lease, (current, currentState) =>
            {
                current.Steps = 0;
                current.WindowStartedAt = store.Now;
                currentState.WindowSavedChanges = current.SavedChanges;
                currentState.FinalCallMade = false;
                return Task.CompletedTask;
            }, cancellationToken);
            return;
        }

        if (timeExpired)
        {
            await AtomicAsync(run, lease, (current, currentState) => PauseAsync(
                current, currentState, "This request used its time allowance. Resume to continue from what is saved.", cancellationToken), cancellationToken);
            return;
        }

        var facts = state.Facts!;
        var snapshot = await conversation.LoadAsync(run.ThreadId, cancellationToken);
        var history = conversation.Build(snapshot, run, state);
        var prune = conversation.PlanPruning(snapshot, AssistantConversation.EstimateTokens(history), run.Id);
        if (prune.Count > 0)
        {
            await AtomicAsync(run, lease, async (_, _) =>
            {
                var now = store.Now;
                await db.AssistantParts.Where(part => prune.Contains(part.Id))
                    .ExecuteUpdateAsync(set => set.SetProperty(part => part.CompactedAt, now), cancellationToken);
            }, cancellationToken, release: false);
            snapshot = await conversation.LoadAsync(run.ThreadId, cancellationToken);
            history = conversation.Build(snapshot, run, state);
        }

        if (conversation.PlanSummary(snapshot, run, AssistantConversation.EstimateTokens(history)) is int through)
        {
            await SummarizeAsync(run, lease, snapshot, through, cancellationToken);
            return;
        }

        var finalCall = run.Steps + 1 >= settings.MaxModelCalls;
        var created = await CreatedQuizzesAsync(run, state, cancellationToken);
        var request = new AgentRequest(
            AssistantPrompts.System(facts.Mode),
            history,
            toolbox.Declarations(facts.Mode),
            facts.Profile,
            CaptureEffectiveRequest: analytics.CaptureContent,
            DurableExecution: true)
        {
            TrailingInstruction = AssistantRunContext.RunNote(run, state, AssistantRunStore.ReadPlan(run.PlanJson), created, finalCall),
            ToolChoice = finalCall ? AgentToolChoice.None : AgentToolChoice.Auto,
        };
        var fingerprints = await FingerprintsAsync(state, facts, cancellationToken);
        var invocationId = Guid.NewGuid();
        var startedAt = store.Now;
        var timer = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, settings.WindowSeconds - (store.Now - run.WindowStartedAt).TotalSeconds)));
        AgentTurnResult response;
        try
        {
            AssistantRunTelemetry.ModelCalls.Add(1);
            response = await model.RunAgentTurnAsync(
                request,
                new AiUsageContext(run.UserId, AiUsageFeatures.Assistant, "assistant_run", invocationId, "assistant_run", run.Id.ToString(), run.TurnId),
                timeout.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await RecordInvocationAsync(run, state.InvocationSequence, invocationId, startedAt, timer.Elapsed, request, ex, cancellationToken);
            throw;
        }

        await AtomicAsync(run, lease, async (current, currentState) =>
        {
            var now = store.Now;
            var usage = response.Metadata?.Usage;
            current.Steps++;
            current.TotalSteps++;
            current.InputTokens += usage?.PromptTokens ?? 0;
            current.CachedInputTokens += usage?.CachedPromptTokens ?? 0;
            current.OutputTokens += usage?.CandidateTokens ?? 0;
            currentState.Step++;
            currentState.LastInputTokens = usage?.PromptTokens ?? 0;
            foreach (var (quizId, fingerprint) in fingerprints)
            {
                currentState.Fingerprints[quizId] = fingerprint;
            }

            var messageId = current.CurrentMessageId!.Value;
            var sequence = await store.NextPartSequenceAsync(messageId, cancellationToken);
            db.AssistantParts.Add(new AssistantPart
            {
                Id = Guid.NewGuid(),
                MessageId = messageId,
                RunId = current.Id,
                Sequence = sequence++,
                Step = currentState.Step,
                Type = AssistantPartTypes.Step,
                ProviderJson = RunJson.Write(response.OutputItemsJson),
                CreatedAt = now,
            });
            if (!string.IsNullOrWhiteSpace(response.Text))
            {
                db.AssistantParts.Add(new AssistantPart
                {
                    Id = Guid.NewGuid(),
                    MessageId = messageId,
                    RunId = current.Id,
                    Sequence = sequence++,
                    Step = currentState.Step,
                    Type = AssistantPartTypes.Text,
                    Text = response.Text.Trim(),
                    CreatedAt = now,
                });
                var message = await db.AssistantMessages.SingleAsync(candidate => candidate.Id == messageId, cancellationToken);
                message.ContentJson = RunJson.Text(AssistantMessageRole.Model, response.Text.Trim()).ContentJson;
            }

            foreach (var (call, index) in response.FunctionCalls.Select((call, index) => (call, index)))
            {
                db.AssistantParts.Add(new AssistantPart
                {
                    Id = Guid.NewGuid(),
                    MessageId = messageId,
                    RunId = current.Id,
                    Sequence = sequence++,
                    Step = currentState.Step,
                    Type = AssistantPartTypes.Tool,
                    ToolName = Clip(call.Name, 64),
                    CallId = call.CallId ?? $"call-{currentState.Step}-{index}",
                    InputJson = call.ArgsJson,
                    // Tools were disabled for the last call. Anything it asked for anyway still
                    // gets a result, so the provider history stays paired.
                    State = finalCall ? AssistantToolStates.Superseded : AssistantToolStates.Pending,
                    Output = finalCall ? RunJson.Write(new { note = "Not run: tools were disabled for this reply." }) : null,
                    CreatedAt = now,
                });
            }

            currentState.Phase = response.FunctionCalls.Count > 0 && !finalCall ? RunPhases.Tools : RunPhases.Finish;
            currentState.FinalCallMade |= finalCall;
            currentState.Invocations[currentState.Step] = invocationId;
            AddInvocation(current, currentState.InvocationSequence++, invocationId, startedAt, timer.Elapsed, request, response, null);
        }, cancellationToken);
    }

    private async Task SummarizeAsync(
        AssistantRun run,
        Guid lease,
        AssistantConversation.Snapshot snapshot,
        int through,
        CancellationToken cancellationToken)
    {
        var request = new AgentRequest(
            AssistantPrompts.Compaction,
            [RunJson.Text("user", conversation.Transcript(snapshot, through))],
            [],
            AssistantAgentProfile.General,
            DurableExecution: true)
        {
            ToolChoice = AgentToolChoice.None,
        };
        var response = await model.RunAgentTurnAsync(
            request,
            new AiUsageContext(run.UserId, AiUsageFeatures.Assistant, "assistant_compaction", Guid.NewGuid(), "assistant_run", run.Id.ToString(), run.TurnId),
            cancellationToken);
        await AtomicAsync(run, lease, async (current, _) =>
        {
            db.AssistantParts.Add(new AssistantPart
            {
                Id = Guid.NewGuid(),
                MessageId = current.UserMessageId,
                RunId = current.Id,
                Sequence = await store.NextPartSequenceAsync(current.UserMessageId, cancellationToken),
                Type = AssistantPartTypes.Summary,
                Text = response.Text.Trim(),
                MetadataJson = RunJson.Write(new { through_sequence = through }),
                CreatedAt = store.Now,
            });
            current.InputTokens += response.Metadata?.Usage.PromptTokens ?? 0;
            current.OutputTokens += response.Metadata?.Usage.CandidateTokens ?? 0;
        }, cancellationToken);
    }

    private async Task ExecuteToolsAsync(AssistantRun run, AssistantRunState state, Guid lease, CancellationToken cancellationToken)
    {
        var open = await db.AssistantParts.AsNoTracking()
            .Where(part => part.RunId == run.Id
                && part.Type == AssistantPartTypes.Tool
                && (part.State == AssistantToolStates.Pending
                    || part.State == AssistantToolStates.Approved
                    || part.State == AssistantToolStates.AwaitingApproval
                    || part.State == AssistantToolStates.AwaitingInput))
            .OrderBy(part => part.Step)
            .ThenBy(part => part.Sequence)
            .ToListAsync(cancellationToken);
        if (open.Count == 0)
        {
            await AtomicAsync(run, lease, (_, current) =>
            {
                current.Phase = current.FinishingPlacements ? RunPhases.Finish : RunPhases.Model;
                return Task.CompletedTask;
            }, cancellationToken);
            return;
        }

        var first = open[0];
        if (first.State is AssistantToolStates.AwaitingApproval or AssistantToolStates.AwaitingInput)
        {
            // Only a command settles these. The status tells the client what the run waits for.
            await AtomicAsync(run, lease, (current, _) =>
            {
                current.Status = first.State == AssistantToolStates.AwaitingApproval
                    ? AssistantRunStatus.AwaitingApproval
                    : AssistantRunStatus.AwaitingInput;
                return Task.CompletedTask;
            }, cancellationToken);
            return;
        }

        var facts = state.Facts!;
        var context = contexts.Tools(run, facts);
        if (first.State == AssistantToolStates.Approved)
        {
            var metadata = RunJson.Read<PartMetadata>(first.MetadataJson ?? "{}");
            await ApplyAsync(run, first, metadata.Changes ?? [], metadata.QuizId, metadata.Output, first.Title ?? "Saved changes", lease, cancellationToken);
            return;
        }

        var tool = toolbox.Find(facts.Mode, first.ToolName ?? string.Empty);
        if (tool?.Kind == AssistantToolKind.Read)
        {
            var batch = open
                .TakeWhile(part => part.State == AssistantToolStates.Pending
                    && toolbox.Find(facts.Mode, part.ToolName ?? string.Empty)?.Kind == AssistantToolKind.Read)
                .Take(ReadBatch)
                .ToList();
            var repeated = await RepeatedAsync(run, batch, cancellationToken);
            var results = await Task.WhenAll(batch.Select(part => repeated.Contains(part.Id)
                ? Task.FromResult(RepeatResult(part))
                : ReadInScopeAsync(part, facts.Mode, context, cancellationToken)));
            await AtomicAsync(run, lease, async (current, currentState) =>
            {
                for (var index = 0; index < batch.Count; index++)
                {
                    var part = await db.AssistantParts.SingleAsync(candidate => candidate.Id == batch[index].Id, cancellationToken);
                    if (part.State == AssistantToolStates.Pending)
                    {
                        await SettleAsync(current, currentState, part, results[index], cancellationToken);
                    }
                }
            }, cancellationToken);
            return;
        }

        await ExecuteOneAsync(run, first, tool, context, lease, cancellationToken);
    }

    private async Task ExecuteOneAsync(
        AssistantRun run,
        AssistantPart part,
        IAssistantTool? tool,
        ToolContext context,
        Guid lease,
        CancellationToken cancellationToken)
    {
        ToolResult result;
        if (tool is null)
        {
            result = ToolResult.Fail(
                $"Unknown tool {part.ToolName}",
                $"There is no tool named {part.ToolName}. Use one of the offered tools.");
        }
        else if ((await RepeatedAsync(run, [part], cancellationToken)).Count > 0)
        {
            result = RepeatResult(part);
        }
        else
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ToolTimeout);
            try
            {
                result = await tool.ExecuteAsync(part.InputJson ?? "{}", context, timeout.Token);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Assistant tool {Tool} failed in run {RunId}", part.ToolName, run.Id);
                result = ToolResult.Fail($"{part.ToolName} failed", "The tool failed unexpectedly. Try again, or take a different approach.");
            }
        }

        if (result.IsError || (result.Changes.Count == 0 && result.Effect is null))
        {
            await AtomicAsync(run, lease, async (current, state) =>
                await SettleTrackedAsync(current, state, part.Id, result, cancellationToken), cancellationToken);
            return;
        }

        switch (result.Effect)
        {
            case AskUserEffect ask:
                await AtomicAsync(run, lease, async (current, state) => await AskAsync(current, state, part.Id, result, ask, cancellationToken), cancellationToken);
                return;
            case UpdatePlanEffect plan:
                await AtomicAsync(run, lease, async (current, state) =>
                {
                    current.PlanJson = plan.Items.Count == 0 ? null : RunJson.Write(plan.Items);
                    await SettleTrackedAsync(current, state, part.Id, result, cancellationToken);
                }, cancellationToken);
                return;
            case ReadSourceEffect read:
                await AtomicAsync(run, lease, async (current, state) =>
                {
                    if (read.MessageId is null || read.MessageId == current.UserMessageId)
                        AssistantRunContext.RecordRead(state, read.FromLine, read.ToLine);
                    await SettleTrackedAsync(current, state, part.Id, result, cancellationToken);
                }, cancellationToken);
                return;
        }

        var thread = await db.AssistantThreads.AsNoTracking().SingleAsync(candidate => candidate.Id == run.ThreadId, cancellationToken);
        if (!AssistantPermissions.MustAsk(result.Changes, AssistantPermissions.Rules(thread)))
        {
            await ApplyAsync(run, part, result.Changes, result.QuizId, Serialize(result.Output), result.Title, lease, cancellationToken);
            return;
        }

        await AtomicAsync(run, lease, async (current, state) =>
        {
            var tracked = await db.AssistantParts.SingleAsync(candidate => candidate.Id == part.Id, cancellationToken);
            if (!await ContentUnchangedAsync(current, state, tracked, result.Changes, result.QuizId, cancellationToken))
            {
                return;
            }

            tracked.Title = Clip(result.Title, 500);
            if (current.Mode == AssistantRunModes.Interactive)
            {
                tracked.State = AssistantToolStates.AwaitingApproval;
                tracked.MetadataJson = RunJson.Write(new PartMetadata { Changes = result.Changes, QuizId = result.QuizId, Output = Serialize(result.Output) });
                current.Status = AssistantRunStatus.AwaitingApproval;
                current.Reason = result.Title;
                return;
            }

            // A caller that only waits for the reply reviews with the classic Apply button.
            await ProposeOnMessageAsync(current, result.Changes, cancellationToken, result.QuizId);
            await SettleAsync(current, state, tracked, ToolResult.Ok(result.Title, new
            {
                proposed = true,
                note = "Proposed for the user's review. Nothing is saved until they apply it; say so in your reply.",
            }), cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// Saves changes and journals them for Undo, in one serializable transaction with the
    /// tool part's result, so a change is committed exactly once and never without its record.
    /// </summary>
    private async Task ApplyAsync(
        AssistantRun run,
        AssistantPart part,
        IReadOnlyList<PendingChange> changes,
        Guid? quizId,
        JsonElement? toolOutput,
        string title,
        Guid lease,
        CancellationToken cancellationToken)
    {
        try
        {
            await AtomicAsync(run, lease, async (current, state) =>
            {
                var tracked = await db.AssistantParts.SingleAsync(candidate => candidate.Id == part.Id, cancellationToken);
                if (tracked.State is not (AssistantToolStates.Pending or AssistantToolStates.Approved))
                {
                    return;
                }

                if (!await ContentUnchangedAsync(current, state, tracked, changes, quizId, cancellationToken))
                {
                    return;
                }

                var prepared = await PlaceAsync(current, changes, cancellationToken);
                var applied = await applier.ApplyAsync(quizId, current.UserId, prepared.Changes, cancellationToken);
                foreach (var entry in applied.Journal)
                {
                    db.AssistantChanges.Add(new AssistantChange
                    {
                        Id = Guid.NewGuid(),
                        RunId = current.Id,
                        PartId = tracked.Id,
                        UserId = current.UserId,
                        Sequence = state.ChangeSequence++,
                        Kind = entry.Kind,
                        EntityType = entry.EntityType,
                        EntityId = entry.EntityId,
                        QuizId = entry.QuizId,
                        BeforeJson = entry.Before is null ? null : RunJson.Write(entry.Before),
                        AfterJson = entry.After is null ? null : RunJson.Write(entry.After),
                        CreatedAt = store.Now,
                    });
                }

                current.SavedChanges += applied.Journal.Count;
                AssistantRunTelemetry.SavedChanges.Add(applied.Journal.Count);
                if (applied.CreatedQuizId is Guid createdId)
                {
                    state.CreatedQuizzes.Add(createdId);
                    if (prepared.Collection is Guid collectionId)
                    {
                        state.Placements[createdId] = collectionId;
                    }

                    var created = await db.Quizzes.SingleAsync(quiz => quiz.Id == createdId, cancellationToken);
                    created.ProcessingStatus = "Building";
                    created.ProcessingMessage = "The assistant is still working on this quiz.";
                }

                if ((quizId ?? applied.CreatedQuizId) is Guid touched)
                {
                    await db.SaveChangesAsync(cancellationToken);
                    state.Fingerprints[touched] = await FingerprintAsync(touched, cancellationToken);
                }

                var savedTitle = applied.AnkiCollectionId is not null && changes.Any(change =>
                    change.Kind is PendingChangeKinds.AddAnkiItems or PendingChangeKinds.LinkAnkiQuiz)
                    ? $"{applied.AnkiCardsAdded} Anki cards added · {applied.AnkiAlreadyIncluded} already included"
                        + (changes.Any(change => change.Kind == PendingChangeKinds.LinkAnkiQuiz) ? " · quiz linked for future additions" : string.Empty)
                    : title;
                await SettleAsync(current, state, tracked, ToolResult.Ok(savedTitle, SavedOutput(changes, applied, toolOutput)), cancellationToken, progressed: applied.Journal.Count > 0);
            }, cancellationToken, isolation: IsolationLevel.Serializable);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or QuizNotFoundException
            && ex is not (InsufficientAiCreditsException or MonthlyAiBudgetExceededException or AssistantRunConflictException))
        {
            // The transaction rolled back, so nothing from this call was saved. The model can
            // correct the call; earlier saved calls are untouched.
            db.ChangeTracker.Clear();
            await AtomicAsync(run, lease, async (current, state) =>
                await SettleTrackedAsync(current, state, part.Id, ToolResult.Fail(
                    "Could not save the change",
                    $"The change could not be saved and nothing from this call was stored: {ex.Message}"), cancellationToken),
                cancellationToken);
        }
    }

    /// <summary>Validate the content the model decided against, including after an approval delay.</summary>
    private async Task<bool> ContentUnchangedAsync(
        AssistantRun run,
        AssistantRunState state,
        AssistantPart part,
        IReadOnlyList<PendingChange> changes,
        Guid? quizId,
        CancellationToken cancellationToken)
    {
        if (!changes.Any(change => change.Kind is PendingChangeKinds.EditWord or PendingChangeKinds.EditSentence
            or PendingChangeKinds.DeleteWord or PendingChangeKinds.DeleteSentence))
        {
            return true;
        }

        if (quizId is not Guid target || !state.Fingerprints.TryGetValue(target, out var known))
        {
            await SettleAsync(run, state, part, ToolResult.Fail(
                "Read the quiz first",
                "Read this quiz with list_items or search_items before editing or deleting its content. Nothing was saved."), cancellationToken);
            return false;
        }

        var current = await FingerprintAsync(target, cancellationToken);
        if (current == known)
        {
            return true;
        }

        state.Fingerprints[target] = current;
        await SettleAsync(run, state, part, ToolResult.Fail(
            "The quiz changed first",
            "The quiz changed since you last read it, probably because the user edited it. Nothing was saved. Read it again and redo the change only if it is still needed."), cancellationToken);
        return false;
    }

    private sealed record Placement(IReadOnlyList<PendingChange> Changes, Guid? Collection);

    /// <summary>
    /// A new quiz bound for a public collection is built privately at the root and moved in
    /// when the run finishes, so a half-built quiz is never published.
    /// </summary>
    private async Task<Placement> PlaceAsync(AssistantRun run, IReadOnlyList<PendingChange> changes, CancellationToken cancellationToken)
    {
        var create = changes.FirstOrDefault(change => change.Kind == PendingChangeKinds.CreateQuiz);
        if (create is null
            || !create.Payload.TryGetProperty("collection_id", out var destination)
            || destination.ValueKind != JsonValueKind.String
            || !Guid.TryParse(destination.GetString(), out var collectionId)
            || !await new CollectionVisibility(db).IsCollectionPubliclyReadableAsync(collectionId, cancellationToken))
        {
            return new Placement(changes, null);
        }

        var payload = System.Text.Json.Nodes.JsonNode.Parse(create.Payload.GetRawText())!.AsObject();
        payload["collection_id"] = null;
        var privateCreate = create with { Payload = JsonSerializer.SerializeToElement(payload) };
        return new Placement(changes.Select(change => ReferenceEquals(change, create) ? privateCreate : change).ToList(), collectionId);
    }

    private async Task FinishAsync(AssistantRun run, AssistantRunState state, Guid lease, CancellationToken cancellationToken)
    {
        if (!state.FinalCallMade && !state.FinishingPlacements && state.Reminders < MaxReminders)
        {
            var reminders = new List<string>();
            if (state.Facts?.SourceLines is int lines && AssistantRunContext.UnreadSource(state, lines) is { } unread)
            {
                reminders.Add($"You have not read lines {unread} of the source text. Continue with read_source, or tell the user why you are stopping.");
            }

            var open = AssistantRunStore.ReadPlan(run.PlanJson).Where(item => item.Status is "pending" or "in_progress").ToList();
            if (open.Count > 0)
            {
                reminders.Add($"Your checklist still has unfinished items: {string.Join("; ", open.Select(item => item.Text))}. Continue them, or update the checklist if they are no longer needed.");
            }

            var reminder = string.Join(" ", reminders);
            if (reminders.Count > 0 && reminder != state.LastReminder)
            {
                await AtomicAsync(run, lease, async (current, currentState) =>
                {
                    db.AssistantParts.Add(new AssistantPart
                    {
                        Id = Guid.NewGuid(),
                        MessageId = current.CurrentMessageId!.Value,
                        RunId = current.Id,
                        Sequence = await store.NextPartSequenceAsync(current.CurrentMessageId.Value, cancellationToken),
                        Step = currentState.Step,
                        Type = AssistantPartTypes.Reminder,
                        Text = reminder,
                        CreatedAt = store.Now,
                    });
                    currentState.Reminders++;
                    currentState.LastReminder = reminder;
                    currentState.Phase = RunPhases.Model;
                }, cancellationToken);
                return;
            }
        }

        if (state.Placements.Count > 0)
        {
            await PlaceQuizzesAsync(run, lease, cancellationToken);
            return;
        }

        await AtomicAsync(run, lease, async (current, currentState) =>
        {
            if (currentState.FinalCallMade)
            {
                await PauseAsync(current, currentState, "This request reached its step limit. Resume to continue from what is saved.", cancellationToken, reply: false);
                return;
            }

            await CompleteAsync(current, currentState, cancellationToken);
        }, cancellationToken);
    }

    private async Task PlaceQuizzesAsync(AssistantRun run, Guid lease, CancellationToken cancellationToken)
    {
        var thread = await db.AssistantThreads.AsNoTracking().SingleAsync(candidate => candidate.Id == run.ThreadId, cancellationToken);
        var rules = AssistantPermissions.Rules(thread);
        await AtomicAsync(run, lease, async (current, state) =>
        {
            var awaiting = false;
            foreach (var (quizId, collectionId) in state.Placements.ToList())
            {
                state.Placements.Remove(quizId);
                var quiz = await db.Quizzes.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == quizId && candidate.UserId == current.UserId, cancellationToken);
                var collection = await db.Collections.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == collectionId && candidate.UserId == current.UserId, cancellationToken);
                if (quiz is null || collection is null)
                {
                    continue;
                }

                var move = QuizContent.Change(PendingChangeKinds.MoveQuiz, new
                {
                    kind = PendingChangeKinds.MoveQuiz,
                    quiz_id = quiz.Id,
                    quiz_name = quiz.Name,
                    collection_id = collection.Id,
                    collection_name = collection.Name,
                });
                var title = $"Move quiz “{quiz.Name}” into “{collection.Name}”";
                var isPublic = await new CollectionVisibility(db).IsCollectionPubliclyReadableAsync(collection.Id, cancellationToken);
                if (!isPublic || rules.Contains(PendingChangeKinds.MoveQuiz))
                {
                    var applied = await applier.ApplyAsync(null, current.UserId, [move], cancellationToken);
                    var part = await AddSyntheticToolAsync(current, state, title, AssistantToolStates.Completed, null, cancellationToken);
                    part.Output = RunJson.Write(new { saved = applied.Journal.Count });
                    foreach (var entry in applied.Journal)
                    {
                        db.AssistantChanges.Add(new AssistantChange
                        {
                            Id = Guid.NewGuid(),
                            RunId = current.Id,
                            PartId = part.Id,
                            UserId = current.UserId,
                            Sequence = state.ChangeSequence++,
                            Kind = entry.Kind,
                            EntityType = entry.EntityType,
                            EntityId = entry.EntityId,
                            QuizId = entry.QuizId,
                            BeforeJson = entry.Before is null ? null : RunJson.Write(entry.Before),
                            AfterJson = entry.After is null ? null : RunJson.Write(entry.After),
                            CreatedAt = store.Now,
                        });
                    }

                    current.SavedChanges += applied.Journal.Count;
                    continue;
                }

                if (current.Mode == AssistantRunModes.Interactive)
                {
                    await AddSyntheticToolAsync(current, state, title, AssistantToolStates.AwaitingApproval,
                        RunJson.Write(new PartMetadata { Changes = [move] }), cancellationToken);
                    awaiting = true;
                    continue;
                }

                await ProposeOnMessageAsync(current, [move], cancellationToken);
            }

            if (awaiting)
            {
                state.FinishingPlacements = true;
                state.Phase = RunPhases.Tools;
                current.Status = AssistantRunStatus.AwaitingApproval;
                current.Reason = "The quiz is ready. Approve publishing it into the collection you asked for.";
            }
        }, cancellationToken);
    }

    private async Task<AssistantPart> AddSyntheticToolAsync(
        AssistantRun run,
        AssistantRunState state,
        string title,
        string toolState,
        string? metadata,
        CancellationToken cancellationToken)
    {
        var part = new AssistantPart
        {
            Id = Guid.NewGuid(),
            MessageId = run.CurrentMessageId!.Value,
            RunId = run.Id,
            Sequence = await store.NextPartSequenceAsync(run.CurrentMessageId.Value, cancellationToken),
            Step = state.Step,
            Type = AssistantPartTypes.Tool,
            ToolName = "move_quiz",
            Title = title,
            State = toolState,
            MetadataJson = metadata,
            CreatedAt = store.Now,
            CompletedAt = AssistantToolStates.IsSettled(toolState) ? store.Now : null,
        };
        db.AssistantParts.Add(part);
        return part;
    }

    private async Task AskAsync(
        AssistantRun run,
        AssistantRunState state,
        Guid partId,
        ToolResult result,
        AskUserEffect ask,
        CancellationToken cancellationToken)
    {
        var part = await db.AssistantParts.SingleAsync(candidate => candidate.Id == partId, cancellationToken);
        part.Title = result.Title;
        if (run.Mode == AssistantRunModes.Interactive)
        {
            part.State = AssistantToolStates.AwaitingInput;
            part.MetadataJson = RunJson.Write(new PartMetadata { Question = ask.Question, Options = ask.Options, Multiple = ask.Multiple });
            run.Status = AssistantRunStatus.AwaitingInput;
            run.Reason = ask.Question;
            return;
        }

        // A caller that cannot answer mid-run gets the question as the reply, and answers it
        // with its next message, which starts a new run that sees this one.
        await SettleAsync(run, state, part, ToolResult.Ok(result.Title, new { asked = true }), cancellationToken);
        foreach (var open in await OpenToolPartsAsync(run, cancellationToken))
        {
            open.State = AssistantToolStates.Superseded;
            open.Output = RunJson.Write(new { note = "Not run: waiting for the user's answer." });
        }

        var question = ask.Options.Count == 0
            ? ask.Question
            : ask.Question + "\n" + string.Join("\n", ask.Options.Select(option => "- " + option));
        await AddTextAsync(run, state, question, cancellationToken);
        await CompleteAsync(run, state, cancellationToken);
    }

    private async Task CompleteAsync(AssistantRun run, AssistantRunState state, CancellationToken cancellationToken)
    {
        var now = store.Now;
        var messageId = run.CurrentMessageId!.Value;
        var message = await db.AssistantMessages.SingleAsync(candidate => candidate.Id == messageId, cancellationToken);
        var text = await LatestTextAsync(messageId, cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            text = run.SavedChanges > 0 ? $"Done. Saved {QuizContent.Count(run.SavedChanges, "change")}." : "Done.";
            await AddTextAsync(run, state, text, cancellationToken);
        }

        var linkSequence = await store.NextPartSequenceAsync(messageId, cancellationToken);
        foreach (var quiz in await CreatedQuizzesAsync(run, state, cancellationToken))
        {
            db.AssistantParts.Add(new AssistantPart
            {
                Id = Guid.NewGuid(),
                MessageId = messageId,
                RunId = run.Id,
                Sequence = linkSequence++,
                Step = state.Step + 1,
                Type = AssistantPartTypes.QuizLink,
                Text = quiz.Id.ToString(),
                Title = quiz.Name,
                CreatedAt = now,
            });
        }

        message.ContentJson = RunJson.Text(AssistantMessageRole.Model, text).ContentJson;
        run.Status = AssistantRunStatus.Completed;
        run.Reason = null;
        run.ActiveUserId = null;
        run.CompletedAt = now;
        await store.DropProviderItemsAsync(run, cancellationToken);
        await SetQuizStatusAsync(run, state, "Ready", null, cancellationToken);
        await CloseTurnAsync(run, AssistantTurnStatus.Completed, null, cancellationToken);
        AssistantRunTelemetry.Runs.Add(1, new KeyValuePair<string, object?>("status", run.Status));
    }

    private async Task PauseAsync(AssistantRun run, AssistantRunState state, string reason, CancellationToken cancellationToken, bool reply = true)
    {
        if (run.Mode == AssistantRunModes.Sync)
        {
            // A caller that cannot Resume gets the reason as the reply and the slot back.
            await FailAsync(run, state, reason, $"I stopped before finishing: {reason}", "paused", cancellationToken, keepReply: !reply);
            return;
        }

        run.Status = AssistantRunStatus.Paused;
        run.Reason = reason;
        await SetQuizStatusAsync(run, state, "Paused", reason, cancellationToken);
    }

    private async Task FailAsync(
        AssistantRun run,
        AssistantRunState state,
        string reason,
        string reply,
        string category,
        CancellationToken cancellationToken,
        bool keepReply = false)
    {
        var now = store.Now;
        foreach (var open in await OpenToolPartsAsync(run, cancellationToken))
        {
            open.State = AssistantToolStates.Superseded;
            open.Output = RunJson.Write(new { note = "Not run: the request stopped." });
            open.CompletedAt = now;
        }

        if (run.CurrentMessageId is Guid messageId)
        {
            var message = await db.AssistantMessages.SingleAsync(candidate => candidate.Id == messageId, cancellationToken);
            var text = keepReply ? await LatestTextAsync(messageId, cancellationToken) : null;
            if (text is null)
            {
                text = reply;
                await AddTextAsync(run, state, reply, cancellationToken);
            }

            message.ContentJson = RunJson.Text(AssistantMessageRole.Model, text).ContentJson;
        }

        run.Status = AssistantRunStatus.Failed;
        run.Reason = reason;
        run.ActiveUserId = null;
        run.CompletedAt = now;
        await store.DropProviderItemsAsync(run, cancellationToken);
        await SetQuizStatusAsync(run, state, "Incomplete", reason, cancellationToken);
        await CloseTurnAsync(run, AssistantTurnStatus.Failed, category, cancellationToken);
        AssistantRunTelemetry.Runs.Add(1, new KeyValuePair<string, object?>("status", run.Status));
    }

    private async Task HandleFailureAsync(AssistantRun run, AssistantRunState state, Exception ex, CancellationToken cancellationToken)
    {
        run.Failures++;
        // A failed model call already recorded its invocation under this sequence.
        state.InvocationSequence++;
        if (ex is GenerativeAiStructuredOutputException && run.Failures < 3)
        {
            state.NoProgress++;
            db.AssistantParts.Add(new AssistantPart
            {
                Id = Guid.NewGuid(),
                MessageId = run.CurrentMessageId!.Value,
                RunId = run.Id,
                Sequence = await store.NextPartSequenceAsync(run.CurrentMessageId.Value, cancellationToken),
                Step = state.Step,
                Type = AssistantPartTypes.Reminder,
                Text = "Your last response was cut off or invalid, and none of it was used. Continue with smaller batches.",
                CreatedAt = store.Now,
            });
            run.Reason = "Adjusting after an incomplete response.";
            return;
        }

        if (Transient(ex) && run.Failures < 3)
        {
            run.Status = AssistantRunStatus.RetryWait;
            run.RetryAt = store.Now.Add(RetryDelay(ex, run.Failures));
            run.Reason = "A temporary service problem occurred. Retrying from what is saved.";
            AssistantRunTelemetry.Retries.Add(1);
            if (run.RetryAt >= run.WindowStartedAt.AddSeconds(options.Value.WindowSeconds))
            {
                await PauseAsync(run, state, "The AI service asked to wait longer than this request's time allowance. Resume to continue.", cancellationToken);
            }

            return;
        }

        var reason = ex switch
        {
            InsufficientAiCreditsException => "More AI credits are needed to continue. What is saved is kept.",
            MonthlyAiBudgetExceededException => "The app's AI budget for this month is used up. What is saved is kept.",
            ResourceQuotaException quota => quota.Message,
            _ => "The assistant could not continue. What is saved is kept; you can Resume to try again.",
        };
        await PauseAsync(run, state, reason, cancellationToken);
    }

    private async Task SettleTrackedAsync(AssistantRun run, AssistantRunState state, Guid partId, ToolResult result, CancellationToken cancellationToken)
    {
        var part = await db.AssistantParts.SingleAsync(candidate => candidate.Id == partId, cancellationToken);
        if (part.State is AssistantToolStates.Pending or AssistantToolStates.Approved)
        {
            await SettleAsync(run, state, part, result, cancellationToken);
        }
    }

    private async Task SettleAsync(
        AssistantRun run,
        AssistantRunState state,
        AssistantPart part,
        ToolResult result,
        CancellationToken cancellationToken,
        bool progressed = true)
    {
        var now = store.Now;
        if (!result.IsError && result.QuizId is Guid readQuiz && !state.Fingerprints.ContainsKey(readQuiz))
        {
            state.Fingerprints[readQuiz] = await FingerprintAsync(readQuiz, cancellationToken);
        }

        part.State = result.IsError ? AssistantToolStates.Error : AssistantToolStates.Completed;
        part.Output = RunJson.Write(result.Output ?? new { ok = true });
        part.Title = Clip(result.Title, 500);
        part.CompletedAt = now;
        if (result.IsError)
        {
            state.NoProgress++;
            state.RecentErrors.Add(Clip(ErrorText(result.Output), 300)!);
            if (state.RecentErrors.Count > 5)
            {
                state.RecentErrors.RemoveAt(0);
            }
        }
        else if (toolbox.Find(state.Facts!.Mode, part.ToolName ?? "")?.Kind == AssistantToolKind.Read
            || part.ToolName == "read_source")
        {
            var key = RunJson.Hash((part.ToolName ?? "") + Canonical(part.InputJson));
            var outputHash = RunJson.Hash(part.Output);
            if (state.ReadResults.TryGetValue(key, out var previous) && previous == outputHash)
                state.NoProgress++;
            else
                state.NoProgress = 0;
            state.ReadResults[key] = outputHash;
        }
        else if (progressed && part.ToolName != "update_plan")
        {
            state.NoProgress = 0;
        }

        if (part.CallId is not null && state.Invocations.TryGetValue(part.Step, out var invocationId))
        {
            AddToolExecution(run, state, part, result, invocationId, now);
        }

        if (jev.Value.Enabled && part.CallId is not null)
        {
            await AddEvaluationAsync(run, state, part, cancellationToken);
        }

        if (state.NoProgress >= NoProgressPause)
        {
            await PauseAsync(run, state, "The last several steps made no progress. Reply with guidance, or Resume to let the assistant try again.", cancellationToken);
        }
    }

    private void AddToolExecution(AssistantRun run, AssistantRunState state, AssistantPart part, ToolResult result, Guid invocationId, DateTime now) =>
        db.AssistantToolExecutions.Add(new AssistantToolExecution
        {
            Id = Guid.NewGuid(),
            TurnId = run.TurnId,
            InvocationId = invocationId,
            Sequence = state.ToolSequence++,
            ToolName = part.ToolName ?? "unknown",
            ArgumentsJson = analytics.StoreJson(AssistantAnalyticsJson.RedactSecrets(part.InputJson ?? "{}")),
            ResultJson = analytics.StoreJson(part.Output ?? "{}"),
            Status = result.IsError ? AssistantInvocationStatus.Failed : AssistantInvocationStatus.Completed,
            ErrorCategory = result.IsError ? "tool_error" : null,
            StartedAt = new DateTimeOffset(part.CreatedAt, TimeSpan.Zero),
            CompletedAt = new DateTimeOffset(now, TimeSpan.Zero),
            DurationMs = (now - part.CreatedAt).TotalMilliseconds,
            ProposedChangeCount = result.Changes.Count,
        });

    private async Task AddEvaluationAsync(AssistantRun run, AssistantRunState state, AssistantPart part, CancellationToken cancellationToken)
    {
        var request = RunJson.Read<AssistantRunInput>(run.RequestJson).Message;
        var steering = await db.AssistantParts.AsNoTracking()
            .Where(candidate => candidate.RunId == run.Id
                && candidate.Type == AssistantPartTypes.Text
                && candidate.MessageId != run.UserMessageId
                && db.AssistantMessages.Any(message => message.Id == candidate.MessageId && message.Role == AssistantMessageRole.User))
            .Select(candidate => candidate.Text!)
            .ToListAsync(cancellationToken);
        var recent = await db.AssistantParts.AsNoTracking()
            .Where(candidate => candidate.RunId == run.Id && candidate.Type == AssistantPartTypes.Tool && candidate.Id != part.Id && candidate.Output != null)
            .OrderByDescending(candidate => candidate.Step)
            .ThenByDescending(candidate => candidate.Sequence)
            .Take(6)
            .Select(candidate => new { candidate.ToolName, candidate.State, candidate.Output })
            .ToListAsync(cancellationToken);
        var facts = state.Facts!;
        var snapshot = new ToolDecisionSnapshot(
            request,
            steering,
            new { facts.QuizId, facts.FocusedWordId, freestyle = facts.Mode == AssistantMode.Freestyle, facts.TargetLanguage, requestedContentKind = facts.RequestedContentKind.ToString() },
            toolbox.Tools(facts.Mode).Select(tool => new { tool.Name, tool.Declaration(facts.Mode).Description, kind = tool.Kind.ToString() }).ToArray(),
            part.ToolName ?? string.Empty,
            SafeJson(part.InputJson),
            recent.Select(item => (object)new { item.ToolName, item.State, result = SafeJson(item.Output) }).ToArray(),
            part.Output);
        var json = RunJson.Write(snapshot);
        db.AssistantToolEvaluations.Add(new AssistantToolEvaluation
        {
            Id = Guid.NewGuid(),
            RunId = run.Id,
            PartId = part.Id,
            ToolName = part.ToolName ?? "unknown",
            SnapshotJson = json,
            SnapshotHash = RunJson.Hash(json),
            ResultJson = part.Output,
            CreatedAt = store.Now,
        });
    }

    private async Task<ToolResult> ReadInScopeAsync(AssistantPart part, AssistantMode mode, ToolContext context, CancellationToken cancellationToken)
    {
        // Each read gets its own scope, and so its own DbContext, because reads run in parallel.
        await using var scope = scopes.CreateAsyncScope();
        var tool = scope.ServiceProvider.GetRequiredService<AssistantToolbox>().Find(mode, part.ToolName ?? string.Empty)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ToolTimeout);
        try
        {
            return await tool.ExecuteAsync(part.InputJson ?? "{}", context, timeout.Token);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Assistant read tool {Tool} failed", part.ToolName);
            return ToolResult.Fail($"{part.ToolName} failed", "The tool failed unexpectedly. Try again, or take a different approach.");
        }
    }

    /// <summary>
    /// Calls identical to the previous calls of the same tool in this run. Repeating one call
    /// with the same arguments is the most common way a model loops, so it is answered without
    /// running it.
    /// </summary>
    private async Task<HashSet<Guid>> RepeatedAsync(AssistantRun run, IReadOnlyList<AssistantPart> parts, CancellationToken cancellationToken)
    {
        var repeated = new HashSet<Guid>();
        foreach (var part in parts)
        {
            var previous = await db.AssistantParts.AsNoTracking()
                .Where(candidate => candidate.RunId == run.Id
                    && candidate.Type == AssistantPartTypes.Tool
                    && candidate.Id != part.Id
                    && (candidate.Step < part.Step || candidate.Step == part.Step && candidate.Sequence < part.Sequence)
                    && (candidate.State == AssistantToolStates.Completed || candidate.State == AssistantToolStates.Error))
                .OrderByDescending(candidate => candidate.Step)
                .ThenByDescending(candidate => candidate.Sequence)
                .Take(RepeatLimit)
                .Select(candidate => new { candidate.ToolName, candidate.InputJson })
                .ToListAsync(cancellationToken);
            var input = Canonical(part.InputJson);
            if (previous.Count == RepeatLimit && previous.All(candidate => candidate.ToolName == part.ToolName && Canonical(candidate.InputJson) == input))
            {
                repeated.Add(part.Id);
            }
        }

        return repeated;
    }

    private static ToolResult RepeatResult(AssistantPart part) => ToolResult.Fail(
        $"Skipped a repeated {part.ToolName} call",
        $"This exact {part.ToolName} call already ran {RepeatLimit} times in a row. Use the earlier result, or change your approach.");

    private async Task<IReadOnlyList<AssistantRunArtifact>> CreatedQuizzesAsync(AssistantRun run, AssistantRunState state, CancellationToken cancellationToken) =>
        state.CreatedQuizzes.Count == 0
            ? []
            : await db.Quizzes.AsNoTracking()
                .Where(quiz => state.CreatedQuizzes.Contains(quiz.Id) && quiz.UserId == run.UserId)
                .Select(quiz => new AssistantRunArtifact(quiz.Id, quiz.Name, quiz.ProcessingStatus))
                .ToListAsync(cancellationToken);

    private async Task<Dictionary<Guid, string>> FingerprintsAsync(AssistantRunState state, RunFacts facts, CancellationToken cancellationToken)
    {
        var quizzes = state.Fingerprints.Keys.Concat(state.CreatedQuizzes).ToHashSet();
        if (facts.QuizId is Guid context)
        {
            quizzes.Add(context);
        }

        var result = new Dictionary<Guid, string>();
        foreach (var quiz in quizzes)
        {
            result[quiz] = await FingerprintAsync(quiz, cancellationToken);
        }

        return result;
    }

    /// <summary>A hash of a quiz's content, for noticing the user's edits between a read and a write.</summary>
    private async Task<string> FingerprintAsync(Guid quizId, CancellationToken cancellationToken)
    {
        var words = await db.Words.AsNoTracking().Where(word => word.QuizId == quizId).OrderBy(word => word.Id)
            .Select(word => new { word.Id, word.Lemma, word.Translation }).ToListAsync(cancellationToken);
        var sentences = await db.QuizSentences.AsNoTracking().Where(sentence => sentence.QuizId == quizId).OrderBy(sentence => sentence.Id)
            .Select(sentence => new { sentence.Id, sentence.Text, sentence.Translation }).ToListAsync(cancellationToken);
        return RunJson.Hash(RunJson.Write(new { words, sentences }));
    }

    private async Task<List<AssistantPart>> OpenToolPartsAsync(AssistantRun run, CancellationToken cancellationToken) =>
        await db.AssistantParts
            .Where(part => part.RunId == run.Id
                && part.Type == AssistantPartTypes.Tool
                && (part.State == AssistantToolStates.Pending
                    || part.State == AssistantToolStates.Approved
                    || part.State == AssistantToolStates.AwaitingApproval
                    || part.State == AssistantToolStates.AwaitingInput))
            .ToListAsync(cancellationToken);

    private async Task ProposeOnMessageAsync(AssistantRun run, IReadOnlyList<PendingChange> changes, CancellationToken cancellationToken, Guid? quizId = null)
    {
        var message = await db.AssistantMessages.SingleAsync(candidate => candidate.Id == run.CurrentMessageId, cancellationToken);
        var existing = string.IsNullOrWhiteSpace(message.PendingChangesJson)
            ? []
            : RunJson.Read<List<PendingChange>>(message.PendingChangesJson);
        // A single reply may propose content changes in several quizzes. Keep the target
        // with each change; ContextQuizId remains the fallback for legacy proposals.
        var targeted = quizId is Guid target
            ? changes.Select(change =>
            {
                var payload = System.Text.Json.Nodes.JsonNode.Parse(change.Payload.GetRawText())!.AsObject();
                payload["quiz_id"] = target;
                return change with { Payload = JsonSerializer.SerializeToElement(payload) };
            })
            : changes;
        message.PendingChangesJson = RunJson.Write(existing.Concat(targeted));
        message.ContextQuizId ??= RunJson.Read<AssistantRunInput>(run.RequestJson).ContextQuizId;
    }

    private async Task<AssistantMessage> AddModelMessageAsync(AssistantRun run, CancellationToken cancellationToken)
    {
        var message = new AssistantMessage
        {
            Id = Guid.NewGuid(),
            ThreadId = run.ThreadId,
            TurnId = run.TurnId,
            ContextQuizId = RunJson.Read<AssistantRunInput>(run.RequestJson).ContextQuizId,
            Sequence = await store.NextSequenceAsync(run.ThreadId, cancellationToken),
            Role = AssistantMessageRole.Model,
            ContentJson = RunJson.Write(new { parts = Array.Empty<object>() }),
            Status = AssistantMessageStatus.Active,
            CreatedAt = new DateTimeOffset(store.Now, TimeSpan.Zero),
        };
        db.AssistantMessages.Add(message);
        var thread = await db.AssistantThreads.SingleAsync(candidate => candidate.Id == run.ThreadId, cancellationToken);
        thread.UpdatedAt = message.CreatedAt;
        return message;
    }

    private async Task AddTextAsync(AssistantRun run, AssistantRunState state, string text, CancellationToken cancellationToken)
    {
        var messageId = run.CurrentMessageId!.Value;
        db.AssistantParts.Add(new AssistantPart
        {
            Id = Guid.NewGuid(),
            MessageId = messageId,
            RunId = run.Id,
            Sequence = await store.NextPartSequenceAsync(messageId, cancellationToken),
            // Its own step: a runtime-written reply has no provider items to pair with.
            Step = state.Step + 1,
            Type = AssistantPartTypes.Text,
            Text = text,
            CreatedAt = store.Now,
        });
    }

    /// <summary>The reply's latest text, including a part staged in this checkpoint and not yet saved.</summary>
    private async Task<string?> LatestTextAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var staged = db.ChangeTracker.Entries<AssistantPart>()
            .Where(entry => entry.State == EntityState.Added
                && entry.Entity.MessageId == messageId
                && entry.Entity.Type == AssistantPartTypes.Text)
            .Select(entry => entry.Entity)
            .MaxBy(part => part.Sequence);
        return staged?.Text ?? await db.AssistantParts.AsNoTracking()
            .Where(part => part.MessageId == messageId && part.Type == AssistantPartTypes.Text)
            .OrderByDescending(part => part.Sequence)
            .Select(part => part.Text)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task SetQuizStatusAsync(AssistantRun run, AssistantRunState state, string status, string? message, CancellationToken cancellationToken)
    {
        foreach (var quiz in await db.Quizzes.Where(quiz => state.CreatedQuizzes.Contains(quiz.Id) && quiz.UserId == run.UserId).ToListAsync(cancellationToken))
        {
            quiz.ProcessingStatus = status;
            quiz.ProcessingMessage = message;
        }
    }

    private async Task CloseTurnAsync(AssistantRun run, string status, string? category, CancellationToken cancellationToken)
    {
        var turn = await db.AssistantTurns.SingleOrDefaultAsync(candidate => candidate.Id == run.TurnId, cancellationToken);
        if (turn is null)
        {
            return;
        }

        var now = new DateTimeOffset(store.Now, TimeSpan.Zero);
        turn.Status = status;
        turn.ErrorCategory = category;
        turn.CompletedAt = now;
        turn.ServerDurationMs = (now - new DateTimeOffset(run.CreatedAt, TimeSpan.Zero)).TotalMilliseconds;
        turn.FinalMessageId = run.CurrentMessageId;
        turn.ActualModel = OpenAiModels.Luna;
        turn.ToolCallCount = await db.AssistantParts.CountAsync(part => part.RunId == run.Id && part.Type == AssistantPartTypes.Tool, cancellationToken);
        turn.ProposedChangeCount = run.SavedChanges;
    }

    private async Task RecordInvocationAsync(
        AssistantRun run,
        int sequence,
        Guid id,
        DateTime startedAt,
        TimeSpan elapsed,
        AgentRequest request,
        Exception error,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        AddInvocation(run, sequence, id, startedAt, elapsed, request, null, error);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // Analytics never decide whether the run continues.
            logger.LogWarning(ex, "Could not record a failed assistant model call for run {RunId}", run.Id);
        }

        db.ChangeTracker.Clear();
    }

    private void AddInvocation(
        AssistantRun run,
        int sequence,
        Guid id,
        DateTime startedAt,
        TimeSpan elapsed,
        AgentRequest request,
        AgentTurnResult? response,
        Exception? error)
    {
        var usage = response?.Metadata?.Usage;
        db.AssistantModelInvocations.Add(new AssistantModelInvocation
        {
            Id = id,
            TurnId = run.TurnId,
            Sequence = sequence,
            Profile = request.Profile.ToString(),
            Provider = AiUsageProviders.OpenAi,
            RequestedModel = OpenAiModels.Luna,
            ActualModel = response?.Metadata?.Model ?? OpenAiModels.Luna,
            RequestJson = analytics.CaptureContent && response?.Metadata?.EffectiveRequestJson is { } effective
                ? AssistantAnalyticsJson.RedactSecrets(effective)
                : "{}",
            ResponseJson = response is null ? null : analytics.SerializePayload(new { response.Text, response.FunctionCalls }),
            ProviderResponseId = response?.Metadata?.ResponseId,
            Status = error is null
                ? AssistantInvocationStatus.Completed
                : error is OperationCanceledException ? AssistantInvocationStatus.Cancelled : AssistantInvocationStatus.Failed,
            ErrorCategory = error is null ? null : AssistantAnalyticsErrors.Classify(error),
            StartedAt = new DateTimeOffset(startedAt, TimeSpan.Zero),
            CompletedAt = new DateTimeOffset(startedAt.Add(elapsed), TimeSpan.Zero),
            DurationMs = elapsed.TotalMilliseconds,
            PromptTokens = usage?.PromptTokens,
            CandidateTokens = usage?.CandidateTokens,
            ThoughtTokens = usage?.ThoughtTokens,
            ToolPromptTokens = usage?.ToolPromptTokens,
            TotalTokens = usage?.TotalTokens,
            CachedPromptTokens = usage?.CachedPromptTokens,
        });
    }

    private Task<AssistantRun?> LoadAsync(Guid id, Guid lease, CancellationToken cancellationToken) => db.AssistantRuns.AsNoTracking()
        .SingleOrDefaultAsync(run => run.Id == id && run.LeaseId == lease && run.LeaseUntil > store.Now && run.Status == AssistantRunStatus.Running, cancellationToken);

    /// <summary>
    /// Runs one checkpoint: re-reads the run under its lease, bumps the revision to take the row
    /// lock and fence concurrent commands, applies the change, and commits. The lease is handed
    /// back afterwards unless the step continues.
    /// </summary>
    private async Task AtomicAsync(
        AssistantRun expected,
        Guid lease,
        Func<AssistantRun, AssistantRunState, Task> action,
        CancellationToken cancellationToken,
        bool release = true,
        IsolationLevel isolation = IsolationLevel.ReadCommitted)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(isolation, cancellationToken)
                : null;
            var run = await db.AssistantRuns.SingleOrDefaultAsync(
                candidate => candidate.Id == expected.Id && candidate.LeaseId == lease && candidate.LeaseUntil > store.Now && candidate.Status == AssistantRunStatus.Running,
                cancellationToken);
            if (run is null)
            {
                return;
            }

            // The operation was prepared outside this transaction. A command may have
            // changed its meaning while the model or tool was running under the same lease.
            if (run.Revision != expected.Revision)
            {
                throw new DbUpdateConcurrencyException("The run changed while this step was being prepared.");
            }

            run.Revision++;
            await db.SaveChangesAsync(cancellationToken);
            var state = RunJson.Read<AssistantRunState>(run.StateJson);
            var status = run.Status;
            await action(run, state);
            run.StateJson = RunJson.Write(state);
            run.UpdatedAt = store.Now;
            if (release || run.Status != AssistantRunStatus.Running)
            {
                run.LeaseId = null;
                run.LeaseUntil = store.Now;
            }

            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            // Pruning can checkpoint and continue with the same lease in this step.
            expected.Revision = run.Revision;
            if (run.Status != status)
            {
                AssistantRunTelemetry.Transitions.Add(1, new KeyValuePair<string, object?>("status", run.Status));
            }
        });
        signals.Notify(expected.Id);
    }

    private static object SavedOutput(IReadOnlyList<PendingChange> changes, AssistantApplyResult applied, JsonElement? toolOutput)
    {
        int Requested(string kind) => changes.Count(change => change.Kind == kind)
            + changes.Where(change => change.Kind == PendingChangeKinds.CreateQuiz)
                .Sum(change => kind switch
                {
                    PendingChangeKinds.AddWord => Count(change.Payload, "words"),
                    PendingChangeKinds.AddSentence => Count(change.Payload, "sentences"),
                    _ => 0,
                });
        int Saved(string kind) => applied.Journal.Count(entry => entry.Kind == kind);
        var words = Saved(PendingChangeKinds.AddWord);
        var sentences = Saved(PendingChangeKinds.AddSentence);
        var notAdded = Requested(PendingChangeKinds.AddWord) - words + Requested(PendingChangeKinds.AddSentence) - sentences;
        return new
        {
            saved = applied.Journal.Count,
            words_added = words,
            sentences_added = sentences,
            edited = Saved(PendingChangeKinds.EditWord) + Saved(PendingChangeKinds.EditSentence),
            removed = Saved(PendingChangeKinds.DeleteWord) + Saved(PendingChangeKinds.DeleteSentence),
            not_added = notAdded > 0
                ? $"{notAdded} requested item(s) were already in the quiz, or repeated a sentence, so they were not added again."
                : null,
            quiz_id = applied.CreatedQuizId,
            collection_id = applied.CreatedCollectionId,
            next = applied.CreatedQuizId is not null ? "Add the rest of the content with add_items and this quiz_id." : null,
            anki_collection_id = applied.AnkiCollectionId,
            anki_url = applied.AnkiCollectionId is Guid ankiId ? AnkiTools.Url(ankiId) : null,
            anki_cards_removed = applied.AnkiCardsRemoved,
            anki_quiz_unlinked = applied.AnkiQuizUnlinked,
            anki_selected_items = applied.AnkiSelectedItems,
            anki_cards_added = applied.AnkiCardsAdded,
            anki_already_included = applied.AnkiAlreadyIncluded,
            anki_excluded_cards = applied.AnkiExcludedCards,
            details = toolOutput,
        };

        static int Count(JsonElement payload, string property) =>
            payload.TryGetProperty(property, out var items) && items.ValueKind == JsonValueKind.Array ? items.GetArrayLength() : 0;
    }

    private static JsonElement? Serialize(object? value) =>
        value is null ? null : JsonSerializer.SerializeToElement(value, RunJson.Options);

    private static JsonElement SafeJson(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new { unparsed = json });
        }
    }

    private static string Canonical(string? json)
    {
        try
        {
            return JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(json) ? "{}" : json));
        }
        catch (JsonException)
        {
            return json ?? string.Empty;
        }
    }

    private static string ErrorText(object? output) =>
        output is null ? "The call failed." : JsonSerializer.Serialize(output, RunJson.Options);

    private static string? Clip(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];

    private static TimeSpan RetryDelay(Exception ex, int failures)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is OpenAiTransportException { RetryAfter: { } delay } && delay > TimeSpan.Zero)
            {
                return delay + TimeSpan.FromMilliseconds(Random.Shared.Next(500));
            }
        }

        return TimeSpan.FromSeconds(Math.Pow(2, failures) + Random.Shared.NextDouble());
    }

    private static bool Transient(Exception ex) => ex is HttpRequestException or GenerativeAiTimeoutException
        or GenerativeAiDependencyUnavailableException or GenerativeAiUpstreamException;
}
