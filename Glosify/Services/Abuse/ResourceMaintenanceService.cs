using Glosify.Data;
using Glosify.Services.Speech;
using Glosify.Services.Storage;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Abuse;

public sealed class ResourceMaintenanceService(IServiceScopeFactory scopes, ILogger<ResourceMaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reconcileAt = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<GlosifyContext>();
                if (DateTimeOffset.UtcNow >= reconcileAt)
                {
                    if (await db.Set<ResourceAccountingState>().AnyAsync(x => x.Id == 1 && x.Ready, stoppingToken))
                        await ReconcileCountersAsync(db, stoppingToken);
                    else
                        await RebuildAsync(db, scope.ServiceProvider, stoppingToken);
                    reconcileAt = DateTimeOffset.UtcNow.AddDays(1);
                }
                await CleanupAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Resource accounting maintenance failed; new content remains protected."); }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    internal static async Task ReconcileCountersAsync(GlosifyContext db, CancellationToken ct)
    {
        // Content and its durable snapshots are committed together. Routine repair
        // aggregates that ledger in SQL, without reopening the initial backfill gate
        // or making provider calls while a transaction is open.
        if (!db.Database.IsSqlServer()) return;
        await ResourceAccounting.TransactionAsync(db, async () =>
        {
            await ResourceAccounting.LockAsync(db, "glosify:resource-accounting", ct);
            await db.Database.ExecuteSqlRawAsync("""
                DELETE FROM [ResourceUsage];
                WITH charges AS (
                    SELECT e.[UserId], j.[key] AS [Resource], CONVERT(bigint, j.[value]) AS [Amount]
                    FROM [ResourceEntry] e CROSS APPLY OPENJSON(e.[ChargesJson]) j
                ), scoped AS (
                    SELECT [UserId] AS [Scope], [Resource], [Amount] FROM charges
                    UNION ALL
                    SELECT N'$site', [Resource], [Amount] FROM charges
                    WHERE [Resource] IN (N'content_bytes', N'pdf_bytes')
                )
                INSERT INTO [ResourceUsage] ([Scope], [Resource], [Used])
                SELECT [Scope], [Resource], SUM([Amount]) FROM scoped
                GROUP BY [Scope], [Resource];
                """, ct);
            return true;
        }, ct);
        db.ChangeTracker.Clear();
    }

    internal static async Task RebuildAsync(GlosifyContext db, IServiceProvider services, CancellationToken ct)
    {
        db.AccountingBypass = true;
        var state = await db.Set<ResourceAccountingState>().FindAsync([1], ct);
        if (state is null) { state = new() { Id = 1 }; db.Add(state); }
        await ResourceAccounting.TransactionAsync(db, async () =>
        {
            await ResourceAccounting.LockAsync(db, "glosify:resource-accounting", ct);
            state.Ready = false;
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
        // Readiness gates writes while snapshots are rebuilt; partial backfills are
        // never exposed as usable capacity. Reads and authentication remain available.
        if (db.Database.IsRelational())
        {
            await db.Set<ResourceEntry>().ExecuteDeleteAsync(ct);
            await db.Set<ResourceUsage>().ExecuteDeleteAsync(ct);
        }
        else
        {
            db.RemoveRange(await db.Set<ResourceEntry>().ToListAsync(ct));
            db.RemoveRange(await db.Set<ResourceUsage>().ToListAsync(ct));
            await db.SaveChangesAsync(ct);
        }
        db.ChangeTracker.Clear();
        foreach (var type in db.Model.GetEntityTypes().Where(ResourceAccounting.IsContent))
        {
            var method = typeof(ResourceMaintenanceService).GetMethod(nameof(LoadBatchAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(type.ClrType);
            for (var offset = 0; ; offset += 200)
            {
                var rows = await (Task<List<object>>)method.Invoke(null, [db, type.FindPrimaryKey()!.Properties.Select(p => p.Name).ToArray(), offset, ct])!;
                if (rows.Count == 0) break;
                foreach (var row in rows)
                {
                    if (row is Glosify.Models.Library.BookDocument book && book.FileSizeBytes == 0)
                    {
                        var blob = services.GetRequiredService<GlosifyBlobServiceClient>().GetRequiredDefaultContainer().GetBlobClient(book.BlobName);
                        book.FileSizeBytes = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value.ContentLength;
                        db.Update(book);
                    }
                    var entry = await ResourceAccounting.SnapshotAsync(db, db.Entry(row), ct);
                    db.Add(entry);
                    foreach (var (resource, amount) in ResourceAccounting.Charges(entry.ChargesJson))
                    {
                        foreach (var owner in resource is "content_bytes" or "pdf_bytes" ? new[] { entry.UserId, ResourceAccounting.Site } : [entry.UserId])
                        {
                            var counter = await db.Set<ResourceUsage>().FindAsync([owner, resource], ct);
                            if (counter is null) { counter = new() { Scope = owner, Resource = resource }; db.Add(counter); }
                            counter.Used += amount;
                        }
                    }
                }
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
            }
        }
        state = await db.Set<ResourceAccountingState>().SingleAsync(x => x.Id == 1, ct);
        state.Ready = true;
        await db.SaveChangesAsync(ct);
        db.AccountingBypass = false;
    }

    private static async Task<List<object>> LoadBatchAsync<T>(GlosifyContext db, string[] keys, int offset, CancellationToken ct) where T : class
    {
        var query = db.Set<T>().AsNoTracking().OrderBy(x => EF.Property<object>(x, keys[0]));
        foreach (var key in keys.Skip(1)) query = query.ThenBy(x => EF.Property<object>(x, key));
        return (await query.Skip(offset).Take(200).ToListAsync(ct)).Cast<object>().ToList();
    }

    private static async Task CleanupAsync(IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<GlosifyContext>();
        var now = DateTimeOffset.UtcNow;
        var quota = services.GetRequiredService<ResourceQuotaService>();
        var failedDeletes = 0;
        async Task<bool> DeleteBlobAsync(string name)
        {
            try { await services.GetRequiredService<IBookFileStorage>().DeleteIfExistsAsync(name, ct); return true; }
            catch (Exception) when (!ct.IsCancellationRequested) { failedDeletes++; return false; }
        }
        foreach (var reservation in await db.Set<ResourceReservation>().AsNoTracking().ToListAsync(ct))
        {
            if (reservation.ExpiresAt > now) continue;
            if (reservation.BlobName is not null && !await DeleteBlobAsync(reservation.BlobName)) continue;
            await quota.ReleaseAsync(reservation.Id, reservation.UserId, ct);
        }
        foreach (var request in await db.Set<BlobCleanupRequest>().OrderBy(x => x.CreatedAt).Take(100).ToListAsync(ct))
        {
            if (!await DeleteBlobAsync(request.BlobName)) continue;
            db.Remove(request);
            await db.SaveChangesAsync(ct);
        }
        // Settlements are financial records and are intentionally retained. Use the
        // expiry index and bound each cleanup pass instead of loading their history.
        foreach (var reservation in await db.Set<SpeechBudgetReservation>()
            .Where(x => !x.Settled && x.ExpiresAt <= now).OrderBy(x => x.ExpiresAt).Take(100).AsNoTracking().ToListAsync(ct))
            await services.GetRequiredService<SpeechProviderBudget>().SettleAsync(reservation.Id, true, ct);
        var buckets = await db.Set<SignupBucket>().ToListAsync(ct);
        db.RemoveRange(buckets.Where(x => x.ExpiresAt < now));
        await db.SaveChangesAsync(ct);
        if (failedDeletes > 0) services.GetRequiredService<ILogger<ResourceMaintenanceService>>()
            .LogWarning("{Count} blob cleanup operations remain pending; capacity is retained.", failedDeletes);
        var cutoff = now.AddDays(-30);
        db.AssistantToolExecutions.RemoveRange(await db.AssistantToolExecutions.Where(x => x.StartedAt < cutoff).Take(500).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        db.AssistantTurns.RemoveRange(await db.AssistantTurns.Where(x => x.StartedAt < cutoff
            && !db.AssistantMessages.Any(m => m.TurnId == x.Id)
            && !db.AssistantFeedback.Any(f => f.TurnId == x.Id)
            && !db.AssistantModelInvocations.Any(i => i.TurnId == x.Id)
            && !db.AssistantToolExecutions.Any(t => t.TurnId == x.Id)).Take(500).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        db.AssistantModelInvocations.RemoveRange(await db.AssistantModelInvocations
            .Where(x => x.StartedAt < cutoff && !db.AssistantToolExecutions.Any(t => t.InvocationId == x.Id)).Take(500).ToListAsync(ct));
        db.RealtimeTranslationCaptureEvents.RemoveRange(await db.RealtimeTranslationCaptureEvents.Where(x => x.StoredAt < cutoff).Take(500).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }
}
