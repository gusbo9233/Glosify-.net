using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Glosify.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Glosify.Services.Abuse;

public static class ResourceAccounting
{
    public const string Site = "$site";
    private static readonly HashSet<string> ContentTypes = [
        "Quiz", "Word", "QuizSentence", "Collection", "AnkiCollection", "AnkiQuizLink",
        "AnkiNote", "AnkiCard", "AnkiReview", "QuizAttempt", "QuizAttemptItem",
        "BookDocument", "BookPage", "BookPageTranslation", "AssistantThread", "AssistantMessage",
        "AssistantPendingChange", "AssistantFeedback", "AssistantFeedbackReason", "RealtimeTranslationCaptureEvent",
        "RealtimeTranslationTranscript", "RealtimeTranslationTranscriptSegment",
        "SavedTranslation", "SavedTranslationSession", "BlobCleanupRequest"];
    public static bool IsContent(IEntityType type) => ContentTypes.Contains(type.ClrType.Name);
    public static Dictionary<string, long> Charges(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, long>>(json)!;
    public static string Key(IEntityType type, object?[] values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(type.ClrType.Name + ":" + JsonSerializer.Serialize(values))));
    public static string Key(EntityEntry entry) => Key(entry.Metadata,
        entry.Metadata.FindPrimaryKey()!.Properties.Select(p => entry.Property(p.Name).CurrentValue).ToArray());

    public sealed class ReservedTotal
    {
        public string Scope { get; set; } = "";
        public string Resource { get; set; } = "";
        public long Amount { get; set; }
    }

    internal static async Task<Dictionary<(string Scope, string Resource), long>> ReservedTotalsAsync(
        GlosifyContext db, IEnumerable<(string Scope, string Resource)> requested,
        IEnumerable<Guid> excluded, CancellationToken ct)
    {
        var keys = requested.Distinct().ToArray();
        if (keys.Length == 0) return [];
        var ids = excluded.Distinct().ToArray();
        var now = DateTimeOffset.UtcNow;
        if (db.Database.IsSqlServer())
        {
            var keyJson = JsonSerializer.Serialize(keys.Select(x => new { x.Scope, x.Resource }));
            var excludedJson = JsonSerializer.Serialize(ids);
            var rows = await db.Database.SqlQuery<ReservedTotal>($"""
                SELECT q.[Scope], q.[Resource], SUM(CONVERT(bigint, j.[value])) AS [Amount]
                FROM [ResourceReservation] r
                CROSS APPLY OPENJSON(r.[ChargesJson]) j
                INNER JOIN OPENJSON({keyJson}) WITH ([Scope] nvarchar(450), [Resource] nvarchar(128)) q
                    ON q.[Resource] = j.[key] AND (q.[Scope] = N'$site' OR q.[Scope] = r.[UserId])
                WHERE (r.[ExpiresAt] > {now} OR r.[BlobName] IS NOT NULL)
                    AND NOT EXISTS (SELECT 1 FROM OPENJSON({excludedJson}) e WHERE CONVERT(uniqueidentifier, e.[value]) = r.[Id])
                GROUP BY q.[Scope], q.[Resource]
                """).ToListAsync(ct);
            return rows.ToDictionary(x => (x.Scope, x.Resource), x => x.Amount);
        }
        // SQLite test provider cannot compare DateTimeOffset in SQL.
        var users = keys.Select(x => x.Scope).ToArray();
        var query = db.Set<ResourceReservation>().AsNoTracking();
        if (!users.Contains(Site)) query = query.Where(x => users.Contains(x.UserId));
        var pending = (await query.ToListAsync(ct)).Where(x => !ids.Contains(x.Id)
            && (x.ExpiresAt > now || x.BlobName != null)).ToArray();
        return keys.ToDictionary(key => key, key => pending.Where(x => key.Scope == Site || x.UserId == key.Scope)
            .Sum(x => Charges(x.ChargesJson).GetValueOrDefault(key.Resource)));
    }

    public static async Task LockAsync(GlosifyContext db, string name, CancellationToken ct)
    {
        if (db.Database.IsSqlServer())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource={name}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=5000; IF @r < 0 THROW 51000, 'Resource accounting is busy. Please retry.', 1;", ct);
        }
    }

    public static async Task<T> TransactionAsync<T>(GlosifyContext db, Func<Task<T>> action, CancellationToken ct)
    {
        if (!db.Database.IsRelational() || db.Database.CurrentTransaction is not null)
            return await action();
        // SaveChanges may accept tracked changes before a transaction commit fails.
        // Restore the original unit of work before replaying a transient failure;
        // otherwise the retry could commit counters without the original content.
        var initial = db.ChangeTracker.Entries().Select(e => (Entry: e, e.State,
            Current: e.CurrentValues.Clone(), Original: e.OriginalValues.Clone(),
            Modified: e.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name).ToHashSet())).ToArray();
        var entities = initial.Select(x => x.Entry.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
        var consumed = db.RequestReservations?.Consumed.ToArray();
        var attempt = 0;
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            if (attempt++ > 0)
            {
                foreach (var entry in db.ChangeTracker.Entries().Where(e => !entities.Contains(e.Entity)).ToArray()) entry.State = EntityState.Detached;
                foreach (var item in initial)
                {
                    item.Entry.CurrentValues.SetValues(item.Current);
                    item.Entry.State = item.State;
                    item.Entry.OriginalValues.SetValues(item.Original);
                    if (item.State == EntityState.Modified)
                        foreach (var property in item.Entry.Properties) property.IsModified = item.Modified.Contains(property.Metadata.Name);
                }
                if (consumed is not null) { db.RequestReservations!.Consumed.Clear(); db.RequestReservations.Consumed.UnionWith(consumed); }
            }
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var result = await action();
            await transaction.CommitAsync(ct);
            return result;
        });
    }

    public static async Task<int> SaveAsync(GlosifyContext db, Func<Task<int>> save, CancellationToken ct)
    {
        db.ChangeTracker.DetectChanges();
        if (db.EnforceAccounting && !db.AccountingBypass)
        {
            var deletedUsers = db.ChangeTracker.Entries().Where(e => e.Metadata.ClrType.Name == "ApplicationUser" && e.State == EntityState.Deleted)
                .Select(e => (string)e.Property("Id").CurrentValue!).ToArray();
            var deletedBooks = db.ChangeTracker.Entries<Glosify.Models.Library.BookDocument>().Where(e => e.State == EntityState.Deleted).Select(e => e.Entity).ToList();
            if (deletedUsers.Length > 0) deletedBooks.AddRange(await db.BookDocuments.Where(b => deletedUsers.Contains(b.UserId)).ToListAsync(ct));
            foreach (var book in deletedBooks.DistinctBy(b => b.Id))
            {
                if (db.ChangeTracker.Entries<BlobCleanupRequest>().Any(e => e.Entity.BlobName == book.BlobName)
                    || await db.Set<BlobCleanupRequest>().AnyAsync(x => x.BlobName == book.BlobName, ct)) continue;
                db.Add(new BlobCleanupRequest { Id = Guid.NewGuid(), UserId = book.UserId, BlobName = book.BlobName,
                    Bytes = book.FileSizeBytes, CreatedAt = DateTimeOffset.UtcNow });
            }
        }
        var changes = db.ChangeTracker.Entries().Where(e =>
            IsContent(e.Metadata) && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
            || e.Metadata.ClrType.Name == "ApplicationUser" && e.State == EntityState.Deleted).ToArray();
        if (changes.Length == 0 || db.AccountingBypass || !db.EnforceAccounting) return await save();
        return await TransactionAsync(db, async () =>
        {
            await LockAsync(db, "glosify:resource-accounting", ct);
            if (db.RequireAccountingReady && !await db.Set<ResourceAccountingState>().AnyAsync(x => x.Id == 1 && x.Ready, ct))
                throw new ResourceQuotaException("accounting_initializing", true);

            var deltas = new Dictionary<(string User, string Resource), long>();
            void Delta(string user, string resource, long amount)
            {
                deltas[(user, resource)] = deltas.GetValueOrDefault((user, resource)) + amount;
                if (resource is "content_bytes" or "pdf_bytes")
                    deltas[(Site, resource)] = deltas.GetValueOrDefault((Site, resource)) + amount;
            }
            var removed = new HashSet<string>();
            foreach (var change in changes.Where(x => x.State == EntityState.Deleted))
            {
                var id = Key(change);
                var marker = "|" + id + "|";
                var userDeleted = change.Metadata.ClrType.Name == "ApplicationUser"
                    ? (string)change.Property("Id").CurrentValue! : "";
                var entries = await db.Set<ResourceEntry>().Where(x => x.Id == id || x.CascadeAncestors.Contains(marker)
                    || userDeleted != "" && x.UserId == userDeleted && x.EntityType != "BlobCleanupRequest").ToListAsync(ct);
                foreach (var old in entries.Where(x => removed.Add(x.Id)))
                {
                    foreach (var (resource, amount) in Charges(old.ChargesJson)) Delta(old.UserId, resource, -amount);
                    db.Remove(old);
                }
            }
            foreach (var change in changes.Where(x => x.State is EntityState.Added or EntityState.Modified))
            {
                var snapshot = await SnapshotAsync(db, change, ct);
                var old = await db.Set<ResourceEntry>().FindAsync([snapshot.Id], ct);
                if (old is not null)
                {
                    foreach (var (resource, amount) in Charges(old.ChargesJson)) Delta(old.UserId, resource, -amount);
                    db.Entry(old).CurrentValues.SetValues(snapshot);
                }
                else db.Add(snapshot);
                foreach (var (resource, amount) in Charges(snapshot.ChargesJson)) Delta(snapshot.UserId, resource, amount);
                if (change.State == EntityState.Modified && change.Metadata.GetForeignKeys()
                    .Any(f => f.Properties.Any(p => change.Property(p.Name).IsModified)))
                {
                    var marker = "|" + snapshot.Id + "|";
                    var descendants = await db.Set<ResourceEntry>().Where(x => x.CascadeAncestors.Contains(marker)).ToListAsync(ct);
                    foreach (var descendant in descendants.Where(x => !removed.Contains(x.Id)))
                    {
                        var type = db.Model.GetEntityTypes().Single(t => t.ClrType.Name == descendant.EntityType);
                        var values = JsonSerializer.Deserialize<JsonElement[]>(descendant.EntityKeyJson)!;
                        var keys = type.FindPrimaryKey()!.Properties.Select((p, i) => values[i].Deserialize(p.ClrType)).ToArray();
                        var entity = await db.FindAsync(type.ClrType, keys, ct);
                        if (entity is null || db.Entry(entity).State == EntityState.Deleted) continue;
                        var updated = await SnapshotAsync(db, db.Entry(entity), ct);
                        descendant.CascadeAncestors = updated.CascadeAncestors;
                    }
                }
            }

            // A caller must explicitly claim its reservation. Expired or foreign claims
            // cannot be converted into content, even when capacity has since become free.
            var claims = db.RequestReservations?.Pending.Where(x => !db.RequestReservations.Consumed.Contains(x.Id)
                && deltas.Keys.Any(k => k.User == x.UserId)).ToList() ?? [];
            if (db.ClaimedResourceReservation is { } explicitClaim) claims.Add(explicitClaim);
            foreach (var claim in claims.Distinct())
            {
                var reservation = await db.Set<ResourceReservation>().FindAsync([claim.Id], ct);
                if (reservation is null || reservation.UserId != claim.UserId || reservation.ExpiresAt <= DateTimeOffset.UtcNow)
                    throw new ResourceQuotaException("reservation_expired");
                if (db.KeepResourceReservation && db.ClaimedResourceReservation?.Id == claim.Id)
                {
                    var remaining = Charges(reservation.ChargesJson);
                    foreach (var resource in remaining.Keys.ToArray())
                    {
                        var consumed = Math.Max(0, deltas.GetValueOrDefault((claim.UserId, resource)));
                        if (consumed > remaining[resource]) throw new ResourceQuotaException("transcript_storage");
                        remaining[resource] -= consumed;
                    }
                    reservation.ChargesJson = JsonSerializer.Serialize(remaining);
                }
                else db.Remove(reservation);
            }
            var reservedTotals = await ReservedTotalsAsync(db,
                deltas.Where(x => x.Value > 0).Select(x => x.Key), claims.Select(x => x.Id), ct);
            foreach (var ((scope, resource), amount) in deltas)
            {
                var counter = await db.Set<ResourceUsage>().FindAsync([scope, resource], ct);
                if (counter is null) { counter = new() { Scope = scope, Resource = resource }; db.Add(counter); }
                var reserved = reservedTotals.GetValueOrDefault((scope, resource));
                var next = checked(counter.Used + amount);
                if (amount > 0 && next + reserved > db.AbuseLimits.Limit(resource, scope == Site))
                    throw new ResourceQuotaException(resource, scope == Site);
                counter.Used = Math.Max(0, next);
                if (counter.Used == 0 && resource.StartsWith("quiz:", StringComparison.Ordinal)) db.Remove(counter);
            }
            var result = await save();
            foreach (var claim in claims) db.RequestReservations?.Consumed.Add(claim.Id);
            return result;
        }, ct);
    }

    public static async Task<ResourceEntry> SnapshotAsync(GlosifyContext db, EntityEntry entry, CancellationToken ct)
    {
        var owner = await OwnerAsync(db, entry, new HashSet<string>(), ct)
            ?? throw new InvalidOperationException($"No resource owner for {entry.Metadata.ClrType.Name}.");
        var charges = new Dictionary<string, long>
        {
            ["content_bytes"] = 1024L + entry.Properties.Where(p => p.Metadata.ClrType == typeof(string))
                .Sum(p => 2L * ((string?)p.CurrentValue)?.Length ?? 0)
        };
        var count = entry.Metadata.ClrType.Name switch
        {
            "Quiz" => "quizzes", "Word" or "QuizSentence" => "quiz_items", "BookDocument" => "books",
            "Collection" or "AnkiCollection" => "collections", "AssistantThread" => "chats",
            "RealtimeTranslationTranscript" => "transcripts", "SavedTranslation" => "translations",
            "SavedTranslationSession" => "translation_sessions", _ => null,
        };
        if (count is not null) charges[count] = 1;
        if (count == "quiz_items") charges["quiz:" + entry.Property("QuizId").CurrentValue] = 1;
        if (count == "books") charges["pdf_bytes"] = (long)entry.Property("FileSizeBytes").CurrentValue!;
        if (entry.Entity is BlobCleanupRequest cleanup) charges["pdf_bytes"] = cleanup.Bytes;
        var ancestors = new HashSet<string>();
        await AncestorsAsync(db, entry, ancestors, ct);
        return new ResourceEntry { Id = Key(entry), UserId = owner, EntityType = entry.Metadata.ClrType.Name,
            EntityKeyJson = JsonSerializer.Serialize(entry.Metadata.FindPrimaryKey()!.Properties.Select(p => entry.Property(p.Name).CurrentValue).ToArray()),
            CascadeAncestors = string.Join("", ancestors.Select(x => "|" + x + "|")), ChargesJson = JsonSerializer.Serialize(charges) };
    }

    private static async Task<EntityEntry?> PrincipalAsync(GlosifyContext db, EntityEntry entry, IForeignKey fk, CancellationToken ct)
    {
        var values = fk.Properties.Select(p => entry.Property(p.Name).CurrentValue).ToArray();
        if (values.Any(x => x is null)) return null;
        var tracked = db.ChangeTracker.Entries().FirstOrDefault(e => e.Metadata == fk.PrincipalEntityType
            && fk.PrincipalKey.Properties.Select(p => e.Property(p.Name).CurrentValue).SequenceEqual(values));
        if (tracked is not null) return tracked;
        var principal = await db.FindAsync(fk.PrincipalEntityType.ClrType, values, ct);
        return principal is null ? null : db.Entry(principal);
    }

    private static async Task<string?> OwnerAsync(GlosifyContext db, EntityEntry entry, HashSet<string> visited, CancellationToken ct)
    {
        if (!visited.Add(Key(entry))) return null;
        if (entry.Metadata.FindProperty("UserId") is not null && entry.Property("UserId").CurrentValue is string owner && owner.Length > 0)
            return owner;
        foreach (var fk in entry.Metadata.GetForeignKeys().OrderByDescending(f => f.DeleteBehavior == DeleteBehavior.Cascade))
        {
            if (fk.PrincipalEntityType.ClrType.Name.StartsWith("Identity", StringComparison.Ordinal)) continue;
            var parent = await PrincipalAsync(db, entry, fk, ct);
            if (parent is not null && await OwnerAsync(db, parent, visited, ct) is { } user) return user;
        }
        return null;
    }

    private static async Task AncestorsAsync(GlosifyContext db, EntityEntry entry, HashSet<string> keys, CancellationToken ct)
    {
        foreach (var fk in entry.Metadata.GetForeignKeys().Where(f => f.DeleteBehavior is DeleteBehavior.Cascade or DeleteBehavior.ClientCascade))
        {
            var values = fk.Properties.Select(p => entry.Property(p.Name).CurrentValue).ToArray();
            if (values.Any(x => x is null)) continue;
            if (!keys.Add(Key(fk.PrincipalEntityType, values))) continue;
            var parent = await PrincipalAsync(db, entry, fk, ct);
            if (parent is not null) await AncestorsAsync(db, parent, keys, ct);
        }
    }
}
