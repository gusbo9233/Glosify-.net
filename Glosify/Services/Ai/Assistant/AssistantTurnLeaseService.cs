using Glosify.Data;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant;

internal interface IAssistantTurnLeaseService
{
    Task<Guid?> TryAcquireAsync(Guid threadId, string userId, CancellationToken cancellationToken);
    Task<bool> RenewAsync(Guid threadId, Guid leaseId, CancellationToken cancellationToken);
    Task ReleaseAsync(Guid threadId, Guid leaseId, CancellationToken cancellationToken);
}

internal sealed class AssistantTurnLeaseService(
    IDbContextFactory<GlosifyContext> contextFactory,
    TimeProvider timeProvider) : IAssistantTurnLeaseService
{
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);

    public async Task<Guid?> TryAcquireAsync(
        Guid threadId,
        string userId,
        CancellationToken cancellationToken)
    {
        var leaseId = Guid.NewGuid();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expiresAt = now.Add(LeaseDuration);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var affected = await context.AssistantThreads
            .Where(thread => thread.Id == threadId && thread.UserId == userId)
            .Where(thread => thread.ActiveTurnId == null
                || !thread.ActiveTurnExpiresAt.HasValue
                || thread.ActiveTurnExpiresAt <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(thread => thread.ActiveTurnId, leaseId)
                .SetProperty(thread => thread.ActiveTurnExpiresAt, expiresAt), cancellationToken);

        return affected == 1 ? leaseId : null;
    }

    public async Task<bool> RenewAsync(
        Guid threadId,
        Guid leaseId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expiresAt = now.Add(LeaseDuration);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var affected = await context.AssistantThreads
            .Where(thread => thread.Id == threadId
                && thread.ActiveTurnId == leaseId
                && thread.ActiveTurnExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(thread => thread.ActiveTurnExpiresAt, expiresAt), cancellationToken);
        return affected == 1;
    }

    public async Task ReleaseAsync(
        Guid threadId,
        Guid leaseId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.AssistantThreads
            .Where(thread => thread.Id == threadId && thread.ActiveTurnId == leaseId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(thread => thread.ActiveTurnId, (Guid?)null)
                .SetProperty(thread => thread.ActiveTurnExpiresAt, (DateTime?)null), cancellationToken);
    }
}

public sealed class AssistantTurnInProgressException()
    : InvalidOperationException("Another assistant response is already in progress for this chat. Try again shortly.");
