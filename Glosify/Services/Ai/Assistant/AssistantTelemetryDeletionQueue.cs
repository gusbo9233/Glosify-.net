using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Ai.Assistant;

/// <summary>
/// Stages telemetry purge work in the caller's SQL transaction. Chat deletion uses the
/// thread method today; an account-deletion workflow must call the user method before
/// deleting the Identity user so its trace-to-user mapping still exists.
/// </summary>
internal sealed class AssistantTelemetryDeletionQueue(
    GlosifyContext context,
    TimeProvider timeProvider,
    IOptions<AssistantAnalyticsOptions> options)
{
    private const int LookupBatchSize = 500;

    public async Task QueueThreadAsync(Guid threadId, CancellationToken cancellationToken) =>
        await QueueAsync(
            context.AssistantTurns.Where(turn => turn.ThreadId == threadId),
            cancellationToken);

    public async Task QueueUserAsync(string userId, CancellationToken cancellationToken) =>
        await QueueAsync(
            context.AssistantTurns.Where(turn => context.AssistantThreads
                .Any(thread => thread.Id == turn.ThreadId && thread.UserId == userId)),
            cancellationToken);

    private async Task QueueAsync(
        IQueryable<AssistantTurn> turns,
        CancellationToken cancellationToken)
    {
        var traceIds = await turns
            .Where(turn => turn.TraceId != null)
            .Select(turn => turn.TraceId!)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (traceIds.Count == 0)
        {
            return;
        }

        var tables = options.Value.PurgeTables.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var targets = tables
            .Select(table => new
            {
                TableName = table,
                DimensionName = GetTraceDimension(table),
            })
            .ToArray();
        var existing = new List<(string TableName, string DimensionName, string DimensionValue)>();
        foreach (var traceIdBatch in traceIds.Chunk(LookupBatchSize))
        {
            existing.AddRange(await context.AssistantTelemetryDeletionRequests
                .Where(request => traceIdBatch.Contains(request.DimensionValue)
                    && tables.Contains(request.TableName))
                .Select(request => new ValueTuple<string, string, string>(
                    request.TableName,
                    request.DimensionName,
                    request.DimensionValue))
                .ToListAsync(cancellationToken));
        }

        var existingSet = existing
            .Select(request => $"{request.TableName}\n{request.DimensionName}\n{request.DimensionValue}")
            .Concat(context.AssistantTelemetryDeletionRequests.Local
                .Select(request => $"{request.TableName}\n{request.DimensionName}\n{request.DimensionValue}"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = timeProvider.GetUtcNow();
        context.AssistantTelemetryDeletionRequests.AddRange(
            from target in targets
            from traceId in traceIds
            where !existingSet.Contains($"{target.TableName}\n{target.DimensionName}\n{traceId}")
            select new AssistantTelemetryDeletionRequest
            {
                Id = Guid.NewGuid(),
                TableName = target.TableName,
                DimensionName = target.DimensionName,
                DimensionValue = traceId,
                Status = AssistantTelemetryDeletionStatus.Pending,
                NextAttemptAt = now,
                CreatedAt = now,
            });
    }

    private static string GetTraceDimension(string tableName) =>
        string.Equals(tableName, "AppGenAIContent", StringComparison.OrdinalIgnoreCase)
            ? "TraceId"
            : "OperationId";
}
