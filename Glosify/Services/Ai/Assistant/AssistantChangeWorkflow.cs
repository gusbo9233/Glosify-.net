using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Assistant.Runtime;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace Glosify.Services.Ai.Assistant;

internal sealed class AssistantChangeWorkflow(
    GlosifyContext context,
    IChangeApplier changeApplier,
    AssistantMessagePresenter presenter,
    AssistantThreadStore threads,
    TimeProvider timeProvider)
{
    public async Task<AssistantApplyResult> ApplyAsync(
        Guid messageId,
        string userId,
        CancellationToken cancellationToken)
    {
        if (!context.Database.IsRelational())
        {
            return await ApplyCoreAsync(messageId, userId, cancellationToken);
        }

        var strategy = context.Database.CreateExecutionStrategy();
        try
        {
            return await Microsoft.EntityFrameworkCore.Storage.RelationalExecutionStrategyExtensions
                .ExecuteInTransactionAsync<AssistantApplyResult>(
                strategy,
                async token =>
                {
                    // A commit failure can cause the execution strategy to invoke this
                    // delegate again. Never reuse accepted entity state from the prior try.
                    context.ChangeTracker.Clear();
                    return await ApplyCoreAsync(messageId, userId, token);
                },
                token => context.AssistantMessages
                    .AsNoTracking()
                    .AnyAsync(message => message.Id == messageId
                        && message.Status == AssistantMessageStatus.Applied, token),
                IsolationLevel.ReadCommitted,
                cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The status concurrency token makes one Apply transaction the winner. The
            // losing transaction is rolled back, including any quiz data it attempted.
            context.ChangeTracker.Clear();
            return new AssistantApplyResult(0);
        }
    }

    private async Task<AssistantApplyResult> ApplyCoreAsync(
        Guid messageId,
        string userId,
        CancellationToken cancellationToken)
    {
        var message = await LoadOwnedMessageAsync(messageId, userId, cancellationToken);
        if (message.Status != AssistantMessageStatus.Active)
        {
            return new AssistantApplyResult(0);
        }

        var changes = presenter.ParseStoredChanges(message.PendingChangesJson);
        if (changes.Count == 0)
        {
            return new AssistantApplyResult(0);
        }

        if (changes.Any(RetiredPendingChangeKinds.ContainsCustomQuizChange)
            || changes.Any(change => LearningChanges.IsRetiredSharing(change.Kind)
                || RetiredPendingChangeKinds.IsAnkiCollectionChange(change.Kind)))
        {
            message.Status = AssistantMessageStatus.Rejected;
            await UpdateTurnOutcomeAsync(message.TurnId, AssistantChangeOutcome.Rejected, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            return new AssistantApplyResult(0);
        }

        var run = message.TurnId is null ? null : await context.AssistantRuns.SingleOrDefaultAsync(
            run => run.TurnId == message.TurnId && run.ThreadId == message.ThreadId && run.UserId == userId, cancellationToken);
        if (run is not null && (!AssistantRunStatus.IsTerminal(run.Status) || run.UndoneAt is not null))
            throw new AssistantRunConflictException("Wait for this request to finish before applying. An undone request cannot apply more changes.");

        var result = await changeApplier.ApplyAsync(
            message.ContextQuizId,
            userId,
            changes,
            cancellationToken);
        if (run is not null && result.Journal.Count > 0)
            await JournalAsync(run, message, result, cancellationToken);
        message.Status = AssistantMessageStatus.Applied;
        await UpdateTurnOutcomeAsync(message.TurnId, AssistantChangeOutcome.Applied, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        return result;
    }

    private async Task JournalAsync(AssistantRun run, AssistantMessage message, AssistantApplyResult result, CancellationToken ct)
    {
        var state = RunJson.Read<AssistantRunState>(run.StateJson);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var part = new AssistantPart
        {
            Id = Guid.NewGuid(), RunId = run.Id, MessageId = message.Id,
            Sequence = (await context.AssistantParts.Where(p => p.MessageId == message.Id).MaxAsync(p => (int?)p.Sequence, ct) ?? -1) + 1,
            Step = ++state.Step, Type = AssistantPartTypes.Tool, State = AssistantToolStates.Completed,
            Title = "Applied reviewed changes", Output = RunJson.Write(new { saved = result.Journal.Count }), CreatedAt = now,
        };
        context.AssistantParts.Add(part);
        foreach (var entry in result.Journal)
        {
            context.AssistantChanges.Add(new AssistantChange
            {
                Id = Guid.NewGuid(), RunId = run.Id, PartId = part.Id, UserId = run.UserId,
                Sequence = state.ChangeSequence++, Kind = entry.Kind, EntityType = entry.EntityType,
                EntityId = entry.EntityId, QuizId = entry.QuizId,
                BeforeJson = entry.Before is null ? null : RunJson.Write(entry.Before),
                AfterJson = entry.After is null ? null : RunJson.Write(entry.After), CreatedAt = now,
            });
            if (entry.Kind == PendingChangeKinds.CreateQuiz && Guid.TryParse(entry.EntityId, out var id))
                state.CreatedQuizzes.Add(id);
        }
        AssistantRunChangeState.RemoveDeletedResources(state, result.Journal);
        run.SavedChanges += result.Journal.Count;
        run.Revision++;
        run.UpdatedAt = now;
        run.StateJson = RunJson.Write(state);
        AssistantRunTelemetry.SavedChanges.Add(result.Journal.Count);
    }

    public async Task RejectAsync(
        Guid messageId,
        string userId,
        CancellationToken cancellationToken)
    {
        var message = await LoadOwnedMessageAsync(messageId, userId, cancellationToken);
        if (message.Status != AssistantMessageStatus.Active)
        {
            return;
        }

        message.Status = AssistantMessageStatus.Rejected;
        await UpdateTurnOutcomeAsync(message.TurnId, AssistantChangeOutcome.Rejected, cancellationToken);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another status transition won after this request loaded the message.
            context.ChangeTracker.Clear();
        }
    }

    public async Task ResetAsync(string userId, CancellationToken cancellationToken)
    {
        await threads.CreateAsync(userId, null, null, null, cancellationToken);
    }

    private async Task<AssistantMessage> LoadOwnedMessageAsync(
        Guid messageId,
        string userId,
        CancellationToken cancellationToken)
    {
        var message = await context.AssistantMessages
            .FirstOrDefaultAsync(candidate => candidate.Id == messageId, cancellationToken)
            ?? throw new InvalidOperationException("Message not found.");
        var thread = await context.AssistantThreads
            .FirstOrDefaultAsync(candidate => candidate.Id == message.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("Chat not found.");
        if (thread.UserId != userId)
        {
            throw new UnauthorizedAccessException("Message belongs to a different user.");
        }

        return message;
    }

    private async Task UpdateTurnOutcomeAsync(
        Guid? turnId,
        string outcome,
        CancellationToken cancellationToken)
    {
        if (!turnId.HasValue)
        {
            return;
        }

        var turn = await context.AssistantTurns
            .SingleOrDefaultAsync(candidate => candidate.Id == turnId.Value, cancellationToken);
        if (turn is null)
        {
            return;
        }

        turn.ChangeOutcome = outcome;
        turn.ChangeOutcomeAt = timeProvider.GetUtcNow();
    }
}
