using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Assistant.Tools;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant.Runtime;

/// <summary>
/// Starts runs, answers what a run is doing, and applies the user's commands to it. The
/// worker does the work; this is the side HTTP requests talk to.
/// </summary>
/// <remarks>
/// Every write increments the run's revision, which is a concurrency token. A command that
/// commits between the worker's read and its checkpoint therefore makes the checkpoint fail,
/// so the worker never overwrites a Stop, a steering message, or a decision.
/// </remarks>
internal sealed class AssistantRunStore(
    GlosifyContext db,
    AssistantContextResolver resolver,
    AssistantMessagePresenter presenter,
    AssistantRunSignals signals,
    TimeProvider clock)
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    internal DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<AssistantRunView> StartAsync(
        Guid threadId,
        string userId,
        AssistantRunStartInput input,
        CancellationToken cancellationToken,
        string mode = AssistantRunModes.Interactive)
    {
        if (string.IsNullOrWhiteSpace(input.IdempotencyKey) || input.IdempotencyKey.Length > 100
            || string.IsNullOrWhiteSpace(input.Request.Message) || input.Request.Message.Length > 50_000)
        {
            throw new ArgumentException("Send a message of at most 50,000 characters with a valid idempotency key.");
        }

        var requestJson = RunJson.Write(input.Request with { Message = input.Request.Message.Trim() });
        var hash = RunJson.Hash(RunJson.Write(new { threadId, requestJson, mode }));
        var existing = await db.AssistantRuns.AsNoTracking()
            .SingleOrDefaultAsync(run => run.UserId == userId && run.IdempotencyKey == input.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            return existing.RequestHash == hash
                ? await ViewAsync(existing.Id, userId, cancellationToken)
                : throw new AssistantRunConflictException("This submission key was already used for a different request.");
        }

        var thread = await db.AssistantThreads
            .SingleOrDefaultAsync(candidate => candidate.Id == threadId && candidate.UserId == userId && candidate.QuizId == null, cancellationToken)
            ?? throw new KeyNotFoundException("Chat not found.");
        var language = await resolver.ResolveLanguageAsync(userId, cancellationToken);
        if (language is not null && thread.Language is not null && thread.Language != language)
        {
            throw new ArgumentException($"That chat belongs to another language. Reload the page to start a {language} chat.");
        }

        await StopWaitingElsewhereAsync(userId, threadId, cancellationToken);
        var now = Now;
        var message = input.Request.Message.Trim();
        var userMessage = new AssistantMessage
        {
            Id = Guid.NewGuid(),
            ThreadId = threadId,
            ContextQuizId = input.Request.ContextQuizId,
            Sequence = await NextSequenceAsync(threadId, cancellationToken),
            Role = AssistantMessageRole.User,
            ContentJson = RunJson.Text(AssistantMessageRole.User, message).ContentJson,
            Status = AssistantMessageStatus.Active,
            CreatedAt = new DateTimeOffset(now, TimeSpan.Zero),
        };
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ThreadId = threadId,
            UserId = userId,
            ActiveUserId = userId,
            IdempotencyKey = input.IdempotencyKey,
            RequestHash = hash,
            Mode = mode,
            Status = AssistantRunStatus.Queued,
            RequestJson = requestJson,
            StateJson = RunJson.Write(new AssistantRunState()),
            TurnId = Guid.NewGuid(),
            UserMessageId = userMessage.Id,
            CreatedAt = now,
            UpdatedAt = now,
            WindowStartedAt = now,
            LeaseUntil = now,
        };
        userMessage.TurnId = run.TurnId;
        // The turn exists from the start so the request's messages can reference it; the run
        // fills in its profile, intent, and languages once it has resolved them.
        db.AssistantTurns.Add(new AssistantTurn
        {
            Id = run.TurnId,
            ThreadId = threadId,
            Profile = input.Request.ContextQuizId is null ? "Librarian" : "QuizAssistant",
            RequestedModel = Glosify.Services.Ai.Generation.OpenAiModels.Luna,
            Status = AssistantTurnStatus.Started,
            StartedAt = userMessage.CreatedAt,
            PromptVersion = AssistantPrompts.Version,
        });
        db.AssistantMessages.Add(userMessage);
        db.AssistantParts.Add(new AssistantPart
        {
            Id = Guid.NewGuid(),
            MessageId = userMessage.Id,
            RunId = run.Id,
            Type = AssistantPartTypes.Text,
            Text = message,
            CreatedAt = now,
        });
        db.AssistantRuns.Add(run);
        if (string.Equals(thread.Title, AssistantThreadDefaults.NewChatTitle, StringComparison.OrdinalIgnoreCase))
        {
            thread.Title = presenter.NormalizeTitle(message);
        }

        thread.UpdatedAt = userMessage.CreatedAt;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            existing = await db.AssistantRuns.AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.UserId == userId && candidate.IdempotencyKey == input.IdempotencyKey, cancellationToken);
            if (existing?.RequestHash == hash)
            {
                return await ViewAsync(existing.Id, userId, cancellationToken);
            }

            throw new AssistantRunConflictException(
                await db.AssistantRuns.AnyAsync(candidate => candidate.ActiveUserId == userId, cancellationToken)
                    ? "The assistant is already working on a request. Wait for it, or stop it first."
                    : "Another request was sent at the same time. Reload the chat.");
        }

        signals.Notify(run.Id);
        return await ViewAsync(run.Id, userId, cancellationToken);
    }

    /// <summary>
    /// A new request supersedes a run that is only waiting on the user in another chat. A run
    /// still working, or anything active in this chat, is left alone and the start is refused.
    /// </summary>
    private async Task StopWaitingElsewhereAsync(string userId, Guid threadId, CancellationToken cancellationToken)
    {
        var active = await db.AssistantRuns.SingleOrDefaultAsync(run => run.ActiveUserId == userId, cancellationToken);
        if (active is null)
        {
            return;
        }

        if (active.ThreadId == threadId || !AssistantRunStatus.IsWaiting(active.Status))
        {
            throw new AssistantRunConflictException(active.ThreadId == threadId
                ? "The assistant is still working on the previous request in this chat. Send your message as a follow-up, or stop it first."
                : "The assistant is working on a request in another chat. Wait for it, or stop it first.");
        }

        await StopAsync(active, "Stopped because a new request started in another chat. Saved changes are kept.", cancellationToken);
    }

    public async Task<AssistantRunView?> LatestAsync(Guid threadId, string userId, CancellationToken cancellationToken)
    {
        if (!await db.AssistantThreads.AnyAsync(thread => thread.Id == threadId && thread.UserId == userId, cancellationToken))
        {
            throw new KeyNotFoundException("Chat not found.");
        }

        var id = await db.AssistantRuns.AsNoTracking()
            .Where(run => run.ThreadId == threadId && run.UserId == userId)
            .OrderByDescending(run => run.CreatedAt)
            .Select(run => (Guid?)run.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return id is Guid runId ? await ViewAsync(runId, userId, cancellationToken) : null;
    }

    public async Task<AssistantRunView> ViewAsync(Guid id, string userId, CancellationToken cancellationToken)
    {
        var run = await db.AssistantRuns.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.UserId == userId, cancellationToken)
            ?? throw new KeyNotFoundException("Request not found.");
        var state = RunJson.Read<AssistantRunState>(run.StateJson);
        var parts = await db.AssistantParts.AsNoTracking()
            .Where(part => part.RunId == run.Id
                && (part.Type == AssistantPartTypes.Text || part.Type == AssistantPartTypes.Tool || part.Type == AssistantPartTypes.QuizLink)
                && db.AssistantMessages.Any(message => message.Id == part.MessageId && message.Role == AssistantMessageRole.Model))
            .Join(db.AssistantMessages, part => part.MessageId, message => message.Id, (part, message) => new { part, message.Sequence })
            .OrderBy(row => row.Sequence)
            .ThenBy(row => row.part.Sequence)
            .Select(row => row.part)
            .ToListAsync(cancellationToken);
        var reviews = await db.AssistantToolEvaluations.AsNoTracking()
            .Where(evaluation => evaluation.RunId == run.Id)
            .ToDictionaryAsync(evaluation => evaluation.PartId, cancellationToken);
        var question = parts.LastOrDefault(part => part.State == AssistantToolStates.AwaitingInput);
        var approval = parts.LastOrDefault(part => part.State == AssistantToolStates.AwaitingApproval);
        var artifacts = await db.Quizzes.AsNoTracking()
            .Where(quiz => state.CreatedQuizzes.Contains(quiz.Id) && quiz.UserId == userId)
            .Select(quiz => new AssistantRunArtifact(quiz.Id, quiz.Name, quiz.ProcessingStatus))
            .ToListAsync(cancellationToken);
        var canUndo = AssistantRunStatus.IsTerminal(run.Status)
            && run.UndoneAt is null
            && await db.AssistantChanges.AnyAsync(change => change.RunId == run.Id && change.Status == AssistantChangeStatus.Applied, cancellationToken);
        return new AssistantRunView(
            run.Id,
            run.ThreadId,
            run.Status,
            run.Reason,
            run.Revision,
            run.TurnId,
            run.UserMessageId,
            run.CurrentMessageId,
            run.SavedChanges,
            run.TotalSteps,
            canUndo,
            run.UndoneAt is not null,
            ReadPlan(run.PlanJson),
            question is null ? null : Question(question),
            approval is null ? null : await ApprovalAsync(approval, userId, cancellationToken),
            parts.Select(part => PartView(part, reviews.GetValueOrDefault(part.Id))).ToList(),
            artifacts);
    }

    internal static AssistantPartView PartView(AssistantPart part, AssistantToolEvaluation? review = null) => new(
        part.Id,
        part.MessageId,
        part.Type,
        part.Type is AssistantPartTypes.Text or AssistantPartTypes.QuizLink ? part.Text : null,
        part.ToolName,
        part.Title ?? (part.Type == AssistantPartTypes.Tool ? ToolLabel(part.ToolName) : null),
        part.State,
        review is null
            ? null
            : new
            {
                status = review.Status,
                findings = review.EvaluationJson is null ? (object?)null : RunJson.Read<JsonElement>(review.EvaluationJson),
            });

    /// <summary>A readable placeholder while a call has not produced its own title yet.</summary>
    internal static string ToolLabel(string? tool) => tool switch
    {
        "add_items" => "Adding to the quiz",
        "edit_items" => "Editing the quiz",
        "delete_items" => "Removing from the quiz",
        "create_quiz" => "Creating a quiz",
        "list_items" or "search_items" or "quiz_overview" => "Reading the quiz",
        "list_library" => "Reading the library",
        "get_book_pages" or "search_book_pages" or "list_books" => "Reading the book",
        "get_saved_transcript" or "list_saved_transcripts" => "Reading the transcript",
        "read_source" => "Reading the source text",
        "update_plan" => "Updating the plan",
        "ask_user" => "Asking a question",
        _ => "Working",
    };

    internal static IReadOnlyList<AssistantPlanItem> ReadPlan(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : RunJson.Read<List<AssistantPlanItem>>(json);

    private static AssistantQuestionView Question(AssistantPart part)
    {
        var metadata = RunJson.Read<PartMetadata>(part.MetadataJson ?? "{}");
        return new AssistantQuestionView(part.Id, metadata.Question ?? string.Empty, metadata.Options ?? [], metadata.Multiple);
    }

    private async Task<AssistantApprovalView> ApprovalAsync(AssistantPart part, string userId, CancellationToken cancellationToken)
    {
        var metadata = RunJson.Read<PartMetadata>(part.MetadataJson ?? "{}");
        var changes = metadata.Changes ?? [];
        var wordIds = presenter.GetReferencedWordIds(changes);
        var labels = await db.Words.AsNoTracking()
            .Where(word => wordIds.Contains(word.Id) && db.Quizzes.Any(quiz => quiz.Id == word.QuizId && quiz.UserId == userId))
            .Select(word => new AssistantWordLabel(word.Id, word.Lemma, word.Translation))
            .ToDictionaryAsync(word => word.Id, cancellationToken);
        return new AssistantApprovalView(
            part.Id,
            changes.Select(change => presenter.PresentPendingChange(change, labels)).ToList(),
            changes.Select(change => change.Kind).Distinct().ToList());
    }

    public async Task<AssistantRunView> CommandAsync(
        Guid id,
        string userId,
        string command,
        AssistantRunCommand input,
        CancellationToken cancellationToken)
    {
        var key = RunJson.Hash(RunJson.Write(new { command, input }));
        // Stop and steering mean the same at any revision, while a working run advances its
        // revision at every checkpoint, so they apply to the latest row and retry when the
        // worker commits first. Decisions and Resume stay bound to what the user saw.
        var latest = command is "cancel" or "steer";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var view = await TryCommandAsync(id, userId, command, input, key, latest, cancellationToken);
                signals.Notify(id);
                return view;
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
                var run = await OwnedAsync(id, userId, cancellationToken);
                if (RunJson.Read<AssistantRunState>(run.StateJson).AppliedCommands.Contains(key))
                {
                    return await ViewAsync(id, userId, cancellationToken);
                }

                if (!latest || attempt == 5)
                {
                    throw new AssistantRunConflictException("The request changed. Reload to see its current state.");
                }

                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<AssistantRunView> TryCommandAsync(
        Guid id,
        string userId,
        string command,
        AssistantRunCommand input,
        string key,
        bool latest,
        CancellationToken cancellationToken)
    {
        var run = await OwnedAsync(id, userId, cancellationToken);
        var state = RunJson.Read<AssistantRunState>(run.StateJson);
        if (state.AppliedCommands.Contains(key) || command == "cancel" && AssistantRunStatus.IsTerminal(run.Status))
        {
            return await ViewAsync(id, userId, cancellationToken);
        }

        if (!latest && run.Revision != input.Revision)
        {
            throw new AssistantRunConflictException("The request changed. Reload to see its current state.");
        }

        var now = Now;
        switch (command)
        {
            case "cancel":
                await StopAsync(run, "Stopped. Changes already saved are kept.", cancellationToken, state);
                break;
            case "resume" when run.Status == AssistantRunStatus.Paused:
                run.Status = run.RetryAt > now ? AssistantRunStatus.RetryWait : AssistantRunStatus.Queued;
                run.WindowStartedAt = now;
                run.Steps = 0;
                run.Failures = 0;
                run.Reason = null;
                state.NoProgress = 0;
                state.WindowSavedChanges = run.SavedChanges;
                if (state.FinalCallMade)
                {
                    state.Phase = RunPhases.Model;
                    state.FinishingPlacements = false;
                }

                state.FinalCallMade = false;
                state.Reminders = 0;
                break;
            case "approve" when run.Status == AssistantRunStatus.AwaitingApproval:
            {
                var part = await WaitingPartAsync(run, AssistantToolStates.AwaitingApproval, cancellationToken);
                part.State = AssistantToolStates.Approved;
                if (input.Always)
                {
                    await AllowAlwaysAsync(run.ThreadId, RunJson.Read<PartMetadata>(part.MetadataJson ?? "{}").Changes ?? [], cancellationToken);
                }

                run.Status = AssistantRunStatus.Queued;
                run.Reason = null;
                break;
            }
            case "reject" when run.Status == AssistantRunStatus.AwaitingApproval:
            {
                var part = await WaitingPartAsync(run, AssistantToolStates.AwaitingApproval, cancellationToken);
                var reason = string.IsNullOrWhiteSpace(input.Message) ? null : input.Message.Trim();
                part.State = AssistantToolStates.Rejected;
                part.Output = RunJson.Write(new
                {
                    declined = true,
                    note = "The user declined this change, so nothing was saved. Do not try it again unless they ask.",
                    reason,
                });
                part.CompletedAt = now;
                run.Status = AssistantRunStatus.Queued;
                run.Reason = null;
                break;
            }
            case "answer" when run.Status == AssistantRunStatus.AwaitingInput:
                await AnswerAsync(run, input, cancellationToken);
                break;
            case "steer" when !AssistantRunStatus.IsTerminal(run.Status):
                if (run.Status == AssistantRunStatus.AwaitingInput)
                {
                    // Typing while a question is open answers it.
                    await AnswerAsync(run, input, cancellationToken);
                    break;
                }

                if (string.IsNullOrWhiteSpace(input.Message) || input.Message.Length > 50_000)
                {
                    throw new ArgumentException("Send a message of at most 50,000 characters.");
                }

                var message = new AssistantMessage
                {
                    Id = Guid.NewGuid(),
                    ThreadId = run.ThreadId,
                    TurnId = run.TurnId,
                    Sequence = await NextSequenceAsync(run.ThreadId, cancellationToken),
                    Role = AssistantMessageRole.User,
                    ContentJson = RunJson.Text(AssistantMessageRole.User, input.Message.Trim()).ContentJson,
                    Status = AssistantMessageStatus.Active,
                    CreatedAt = new DateTimeOffset(now, TimeSpan.Zero),
                };
                db.AssistantMessages.Add(message);
                db.AssistantParts.Add(new AssistantPart
                {
                    Id = Guid.NewGuid(),
                    MessageId = message.Id,
                    RunId = run.Id,
                    Type = AssistantPartTypes.Text,
                    Text = input.Message.Trim(),
                    CreatedAt = now,
                });
                state.PendingSteering.Add(message.Id);
                if (AssistantRunStatus.IsWaiting(run.Status))
                {
                    run.Status = AssistantRunStatus.Queued;
                    run.WindowStartedAt = now;
                    run.Steps = 0;
                    run.Reason = null;
                }

                break;
            default:
                throw new AssistantRunConflictException("That action is not available right now. Reload to see the request's current state.");
        }

        state.AppliedCommands.Add(key);
        run.StateJson = RunJson.Write(state);
        run.Revision++;
        run.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return await ViewAsync(id, userId, cancellationToken);
    }

    private async Task AnswerAsync(AssistantRun run, AssistantRunCommand input, CancellationToken cancellationToken)
    {
        var answers = (input.Answers ?? []).Where(answer => !string.IsNullOrWhiteSpace(answer)).Select(answer => answer.Trim()).ToList();
        if (!string.IsNullOrWhiteSpace(input.Message))
        {
            answers.Add(input.Message.Trim());
        }

        if (answers.Count == 0)
        {
            throw new ArgumentException("Choose or type an answer.");
        }

        var part = await WaitingPartAsync(run, AssistantToolStates.AwaitingInput, cancellationToken);
        var metadata = RunJson.Read<PartMetadata>(part.MetadataJson ?? "{}");
        part.MetadataJson = RunJson.Write(metadata with { Answers = answers });
        part.State = AssistantToolStates.Completed;
        part.Output = RunJson.Write(new { question = metadata.Question, answer = string.Join(", ", answers) });
        part.CompletedAt = Now;
        run.Status = AssistantRunStatus.Queued;
        run.WindowStartedAt = Now;
        run.Steps = 0;
        run.Reason = null;
    }

    /// <summary>
    /// Ends a run where it stands. Calls not yet made get a result saying so, which keeps the
    /// provider history well-formed, and saved changes stay in place.
    /// </summary>
    private async Task StopAsync(AssistantRun run, string reason, CancellationToken cancellationToken, AssistantRunState? state = null)
    {
        var now = Now;
        state ??= RunJson.Read<AssistantRunState>(run.StateJson);
        run.Status = AssistantRunStatus.Cancelled;
        run.Reason = reason;
        run.ActiveUserId = null;
        run.LeaseId = null;
        run.LeaseUntil = now;
        run.CompletedAt = now;
        run.Revision++;
        run.UpdatedAt = now;
        var open = await db.AssistantParts
            .Where(part => part.RunId == run.Id
                && part.Type == AssistantPartTypes.Tool
                && (part.State == AssistantToolStates.Pending
                    || part.State == AssistantToolStates.Approved
                    || part.State == AssistantToolStates.AwaitingApproval
                    || part.State == AssistantToolStates.AwaitingInput))
            .ToListAsync(cancellationToken);
        foreach (var part in open)
        {
            part.State = AssistantToolStates.Superseded;
            part.Output = RunJson.Write(new { note = "Not run: the user stopped the request." });
            part.CompletedAt = now;
        }

        if (run.CurrentMessageId is Guid messageId)
        {
            var message = await db.AssistantMessages.SingleAsync(candidate => candidate.Id == messageId, cancellationToken);
            var hasText = await db.AssistantParts.AnyAsync(
                part => part.MessageId == messageId && part.Type == AssistantPartTypes.Text,
                cancellationToken);
            if (!hasText)
            {
                const string stopped = "Stopped before finishing.";
                db.AssistantParts.Add(new AssistantPart
                {
                    Id = Guid.NewGuid(),
                    MessageId = messageId,
                    RunId = run.Id,
                    Sequence = await NextPartSequenceAsync(messageId, cancellationToken),
                    Step = state.Step + 1,
                    Type = AssistantPartTypes.Text,
                    Text = stopped,
                    CreatedAt = now,
                });
                message.ContentJson = RunJson.Text(AssistantMessageRole.Model, stopped).ContentJson;
            }
        }

        foreach (var quiz in await db.Quizzes.Where(quiz => state.CreatedQuizzes.Contains(quiz.Id) && quiz.UserId == run.UserId).ToListAsync(cancellationToken))
        {
            quiz.ProcessingStatus = "Incomplete";
            quiz.ProcessingMessage = reason;
        }

        await DropProviderItemsAsync(run, cancellationToken);

        var turn = await db.AssistantTurns.SingleOrDefaultAsync(candidate => candidate.Id == run.TurnId, cancellationToken);
        if (turn is not null && turn.Status == AssistantTurnStatus.Started)
        {
            turn.Status = AssistantTurnStatus.Cancelled;
            turn.CompletedAt = new DateTimeOffset(now, TimeSpan.Zero);
            turn.FinalMessageId = run.CurrentMessageId;
        }
    }

    /// <summary>
    /// Removes a finished run's raw provider items. They only replay within their own run, and
    /// with encrypted reasoning they are the largest thing a run stores against the user's quota.
    /// </summary>
    internal async Task DropProviderItemsAsync(AssistantRun run, CancellationToken cancellationToken)
    {
        // Tracked removal, so resource accounting releases what they were charged.
        db.AssistantParts.RemoveRange(await db.AssistantParts
            .Where(part => part.RunId == run.Id && part.Type == AssistantPartTypes.Step)
            .ToListAsync(cancellationToken));
    }

    private async Task<AssistantPart> WaitingPartAsync(AssistantRun run, string state, CancellationToken cancellationToken) =>
        await db.AssistantParts
            .Where(part => part.RunId == run.Id && part.State == state)
            .OrderByDescending(part => part.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
        ?? throw new AssistantRunConflictException("Nothing is waiting for that. Reload to see the request's current state.");

    private async Task AllowAlwaysAsync(Guid threadId, IReadOnlyList<PendingChange> changes, CancellationToken cancellationToken)
    {
        var thread = await db.AssistantThreads.SingleAsync(candidate => candidate.Id == threadId, cancellationToken);
        var rules = AssistantPermissions.Rules(thread);
        rules.UnionWith(changes.Select(change => change.Kind).Where(AssistantPermissions.NeedsApproval));
        thread.ApprovalRules = RunJson.Write(rules.Order(StringComparer.Ordinal));
    }

    internal async Task<AssistantRun> OwnedAsync(Guid id, string userId, CancellationToken cancellationToken) =>
        await db.AssistantRuns.SingleOrDefaultAsync(run => run.Id == id && run.UserId == userId, cancellationToken)
        ?? throw new KeyNotFoundException("Request not found.");

    internal async Task<int> NextSequenceAsync(Guid threadId, CancellationToken cancellationToken)
    {
        var stored = await db.AssistantMessages
            .Where(message => message.ThreadId == threadId)
            .Select(message => (int?)message.Sequence)
            .MaxAsync(cancellationToken) ?? -1;
        var staged = db.ChangeTracker.Entries<AssistantMessage>()
            .Where(entry => entry.State == EntityState.Added && entry.Entity.ThreadId == threadId)
            .Select(entry => entry.Entity.Sequence)
            .DefaultIfEmpty(-1)
            .Max();
        return Math.Max(stored, staged) + 1;
    }

    internal async Task<int> NextPartSequenceAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var stored = await db.AssistantParts
            .Where(part => part.MessageId == messageId)
            .Select(part => (int?)part.Sequence)
            .MaxAsync(cancellationToken) ?? -1;
        var staged = db.ChangeTracker.Entries<AssistantPart>()
            .Where(entry => entry.State == EntityState.Added && entry.Entity.MessageId == messageId)
            .Select(entry => entry.Entity.Sequence)
            .DefaultIfEmpty(-1)
            .Max();
        return Math.Max(stored, staged) + 1;
    }

    internal async Task<(Guid Id, Guid Lease)?> ClaimAsync(CancellationToken cancellationToken)
    {
        var now = Now;
        var candidate = await db.AssistantRuns.AsNoTracking()
            .Where(run => (run.Status == AssistantRunStatus.Queued || run.Status == AssistantRunStatus.Running || run.Status == AssistantRunStatus.RetryWait)
                && (run.RetryAt == null || run.RetryAt <= now)
                && run.LeaseUntil <= now)
            .OrderBy(run => run.UpdatedAt)
            .Select(run => (Guid?)run.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (candidate is null)
        {
            return null;
        }

        var lease = Guid.NewGuid();
        var claimed = await db.AssistantRuns
            .Where(run => run.Id == candidate
                && run.LeaseUntil <= now
                && (run.Status == AssistantRunStatus.Queued || run.Status == AssistantRunStatus.Running || run.Status == AssistantRunStatus.RetryWait))
            .ExecuteUpdateAsync(set => set
                .SetProperty(run => run.LeaseId, lease)
                .SetProperty(run => run.LeaseUntil, now.Add(LeaseDuration))
                .SetProperty(run => run.Status, AssistantRunStatus.Running), cancellationToken);
        return claimed == 1 ? (candidate.Value, lease) : null;
    }

    internal Task<int> RenewAsync(Guid id, Guid lease, CancellationToken cancellationToken) => db.AssistantRuns
        .Where(run => run.Id == id && run.LeaseId == lease && run.LeaseUntil > Now && run.Status == AssistantRunStatus.Running)
        .ExecuteUpdateAsync(set => set.SetProperty(run => run.LeaseUntil, Now.Add(LeaseDuration)), cancellationToken);
}

/// <summary>What a tool part carries for the UI and for decisions made after it ran.</summary>
internal sealed record PartMetadata
{
    public IReadOnlyList<PendingChange>? Changes { get; init; }
    public Guid? QuizId { get; init; }
    public JsonElement? Output { get; init; }
    public string? Question { get; init; }
    public IReadOnlyList<string>? Options { get; init; }
    public bool Multiple { get; init; }
    public IReadOnlyList<string>? Answers { get; init; }
}

/// <summary>
/// Which saved changes need the user's approval first. Adds and edits are saved right away
/// and can be undone; deletions and moves, which lose content or can change who sees a quiz,
/// wait unless the user chose to allow that kind in this chat.
/// </summary>
internal static class AssistantPermissions
{
    private static readonly HashSet<string> Ask =
    [
        PendingChangeKinds.DeleteWord,
        PendingChangeKinds.DeleteSentence,
        PendingChangeKinds.MoveQuiz,
        PendingChangeKinds.MoveCollection,
    ];

    public static bool NeedsApproval(string kind) => Ask.Contains(kind);

    public static HashSet<string> Rules(AssistantThread thread) =>
        string.IsNullOrWhiteSpace(thread.ApprovalRules)
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(RunJson.Read<List<string>>(thread.ApprovalRules), StringComparer.Ordinal);

    public static bool MustAsk(IEnumerable<PendingChange> changes, IReadOnlySet<string> allowed) =>
        changes.Any(change => Ask.Contains(change.Kind) && !allowed.Contains(change.Kind));
}
