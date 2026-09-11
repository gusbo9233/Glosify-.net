using System.Text.Json;
using Glosify.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Abuse;

public sealed record ResourceUsageView(string Resource, long Current, long Reserved, long Maximum);

public sealed class ResourceQuotaService(IDbContextFactory<GlosifyContext> factory, IOptions<AbuseOptions> options)
{
    public async Task<Guid> ReserveAsync(string userId, Dictionary<string, long> charges,
        CancellationToken ct = default, string? blobName = null, TimeSpan? lifetime = null)
    {
        if (string.IsNullOrWhiteSpace(userId) || charges.Any(x => x.Value < 0)) throw new ArgumentException("Invalid reservation.");
        var id = Guid.NewGuid();
        await using var db = await factory.CreateDbContextAsync(ct);
        return await ResourceAccounting.TransactionAsync(db, async () =>
        {
            await ResourceAccounting.LockAsync(db, "glosify:resource-accounting", ct);
            if (await db.Set<ResourceReservation>().AnyAsync(x => x.Id == id, ct)) return id;
            if (db.RequireAccountingReady && !await db.Set<ResourceAccountingState>().AnyAsync(x => x.Ready, ct))
                throw new ResourceQuotaException("accounting_initializing", true);
            var reservedTotals = await ResourceAccounting.ReservedTotalsAsync(db,
                charges.Where(x => x.Value > 0).SelectMany(x =>
                    (x.Key is "pdf_bytes" or "content_bytes" ? new[] { userId, ResourceAccounting.Site } : [userId])
                    .Select(scope => (scope, x.Key))), [], ct);
            foreach (var (resource, amount) in charges)
            {
                foreach (var scope in resource is "pdf_bytes" or "content_bytes" ? new[] { userId, ResourceAccounting.Site } : [userId])
                {
                    var counter = await db.Set<ResourceUsage>().FindAsync([scope, resource], ct);
                    var reserved = reservedTotals.GetValueOrDefault((scope, resource));
                    if (amount > 0 && (counter?.Used ?? 0) + reserved + amount > options.Value.Limit(resource, scope == ResourceAccounting.Site))
                        throw new ResourceQuotaException(resource, scope == ResourceAccounting.Site);
                }
            }
            var reservation = new ResourceReservation { Id = id, UserId = userId,
                ChargesJson = JsonSerializer.Serialize(charges), BlobName = blobName,
                ExpiresAt = DateTimeOffset.UtcNow + (lifetime ?? TimeSpan.FromMinutes(5)) };
            db.Add(reservation); await db.SaveChangesAsync(ct); return reservation.Id;
        }, ct);
    }

    internal async Task RenewAsync(Guid id, string userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await ResourceAccounting.TransactionAsync(db, async () =>
        {
            await ResourceAccounting.LockAsync(db, "glosify:resource-accounting", ct);
            var row = await db.Set<ResourceReservation>().SingleOrDefaultAsync(x => x.Id == id, ct);
            // A successful content save may already have consumed the reservation.
            if (row is null) return true;
            if (row.UserId != userId || row.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new ResourceQuotaException("reservation_expired");
            row.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
    }

    public async Task ReleaseAsync(Guid id, string userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await ResourceAccounting.TransactionAsync(db, async () =>
        {
            await ResourceAccounting.LockAsync(db, "glosify:resource-accounting", ct);
            var row = await db.Set<ResourceReservation>().SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
            if (row is not null) { db.Remove(row); await db.SaveChangesAsync(ct); }
            return true;
        }, ct);
    }

    public async Task<IReadOnlyList<ResourceUsageView>> GetUsageAsync(string userId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (db.RequireAccountingReady && !await db.Set<ResourceAccountingState>().AnyAsync(x => x.Ready, ct))
            throw new ResourceQuotaException("accounting_initializing", true);
        var rows = await db.Set<ResourceUsage>().Where(x => x.Scope == userId).AsNoTracking().ToListAsync(ct);
        var reservations = await db.Set<ResourceReservation>().Where(x => x.UserId == userId).AsNoTracking().ToListAsync(ct);
        var pending = reservations.Where(x => x.ExpiresAt > DateTimeOffset.UtcNow || x.BlobName != null)
            .Select(x => ResourceAccounting.Charges(x.ChargesJson)).ToArray();
        long Reserved(string resource) => pending.Sum(x => x.GetValueOrDefault(resource));
        string[] resources = ["quizzes", "quiz_items", "books", "collections", "chats", "transcripts", "translations", "translation_sessions", "pdf_bytes", "content_bytes"];
        var result = resources.Select(resource => new ResourceUsageView(resource,
            rows.SingleOrDefault(x => x.Resource == resource)?.Used ?? 0,
            Reserved(resource), options.Value.Limit(resource))).ToList();
        var fullestQuiz = rows.Select(x => x.Resource).Concat(pending.SelectMany(x => x.Keys))
            .Where(x => x.StartsWith("quiz:", StringComparison.Ordinal)).Distinct()
            .Select(resource => new ResourceUsageView("largest_quiz_items",
                rows.SingleOrDefault(x => x.Resource == resource)?.Used ?? 0, Reserved(resource), options.Value.ItemsPerQuiz))
            .OrderByDescending(x => x.Current + x.Reserved).FirstOrDefault();
        result.Insert(2, fullestQuiz ?? new("largest_quiz_items", 0, 0, options.Value.ItemsPerQuiz));
        return result;
    }
}
