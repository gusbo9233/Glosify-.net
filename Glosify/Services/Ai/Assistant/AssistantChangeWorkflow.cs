using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant;

internal sealed class AssistantChangeWorkflow(
    GlosifyContext context,
    IChangeApplier changeApplier,
    AssistantMessagePresenter presenter,
    AssistantThreadStore threads)
{
    public async Task<AssistantApplyResult> ApplyAsync(
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

        // Claim the message before applying so concurrent Apply requests (for example,
        // a double-click) cannot run the same changes twice. Restore the claim when the
        // apply fails so the user can retry.
        message.Status = AssistantMessageStatus.Applied;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another request changed the active status first. This request did not
            // acquire the claim and must not apply the same change set again.
            context.ChangeTracker.Clear();
            return new AssistantApplyResult(0);
        }

        try
        {
            return await changeApplier.ApplyAsync(message.ContextQuizId, userId, changes, cancellationToken);
        }
        catch
        {
            // Discard changes left in the tracker by the failed operation. Compensation
            // must survive a disconnected client, so it deliberately ignores the request token.
            context.ChangeTracker.Clear();
            var claimed = await context.AssistantMessages
                .FirstOrDefaultAsync(candidate => candidate.Id == messageId, CancellationToken.None);
            if (claimed?.Status == AssistantMessageStatus.Applied)
            {
                claimed.Status = AssistantMessageStatus.Active;
                try
                {
                    await context.SaveChangesAsync(CancellationToken.None);
                }
                catch (DbUpdateConcurrencyException)
                {
                    // A concurrent reject or other status transition wins over recovery.
                    context.ChangeTracker.Clear();
                }
            }

            throw;
        }
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
}
