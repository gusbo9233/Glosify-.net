using System.Text.Json;
using Glosify.Data;
using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Assistant.Runtime;
using Glosify.Services.Ai.Assistant.Tools;
using Glosify.Services.Anki;
using Glosify.Services.Quizzes;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant;

/// <summary>Scoped snapshots for assistant learning-management changes and conflict-safe Undo.</summary>
internal sealed class LearningChanges(GlosifyContext db, IQuizService quizzes, ICollectionService collections, IAnkiCollectionService anki)
{
    internal static readonly string[] QuizActions = ["rename_quiz", "delete_quiz", "delete_collection", "quiz_visibility", "collection_visibility", "copy_quiz", "copy_collection"];
    internal static readonly string[] AnkiActions = ["create", "rename", "delete", "settings", "direction", "add_quiz", "remove_quiz", "add_item", "remove_card"];
    internal static bool Handles(string kind) => QuizActions.Contains(kind) || kind.StartsWith("anki_", StringComparison.Ordinal);
    internal static bool Permanent(string kind) => kind is "delete_quiz" or "delete_collection" or "anki_delete";

    internal static IQueryable<AssistantChange> Undoable(IQueryable<AssistantChange> changes) =>
        changes.Where(change => change.Status == AssistantChangeStatus.Applied
            && change.Kind != "delete_quiz" && change.Kind != "delete_collection" && change.Kind != "anki_delete");
    internal sealed record Row(string Type, string Id, SortedDictionary<string, JsonElement> Values);
    private static readonly Dictionary<string, Type> Types = new()
    {
        [nameof(Quiz)] = typeof(Quiz), [nameof(Collection)] = typeof(Collection),
        [nameof(AnkiCollection)] = typeof(AnkiCollection), [nameof(AnkiQuizLink)] = typeof(AnkiQuizLink),
        [nameof(AnkiNote)] = typeof(AnkiNote), [nameof(AnkiCard)] = typeof(AnkiCard), [nameof(AnkiReview)] = typeof(AnkiReview),
        [nameof(Word)] = typeof(Word), [nameof(QuizSentence)] = typeof(QuizSentence)
    };
    internal static async Task<bool> IsPublicAsync(GlosifyContext db, string type, Guid id, CancellationToken ct)
    {
        if (type == "quiz")
        {
            var quiz = await db.Quizzes.AsNoTracking().SingleOrDefaultAsync(q => q.Id == id, ct);
            if (quiz is null) return false;
            if (quiz.IsPublic) return true;
            if (quiz.CollectionId is not Guid folder) return false;
            id = folder;
        }
        var seen = new HashSet<Guid>();
        while (seen.Add(id))
        {
            var folder = await db.Collections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
            if (folder is null) return false;
            if (folder.IsPublic) return true;
            if (folder.ParentCollectionId is not Guid parent) return false;
            id = parent;
        }
        return false;
    }
    internal static object CreationState(Quiz quiz) => new
    {
        name = quiz.Name, collection_id = quiz.CollectionId, source_language = quiz.SourceLanguage,
        target_language = quiz.TargetLanguage, language = quiz.Language, is_public = quiz.IsPublic,
        is_song_quiz = quiz.IsSongQuiz, original_quiz_id = quiz.OriginalQuizId
    };
    internal static object CreationState(Collection collection) => new
    {
        name = collection.Name, parent_collection_id = collection.ParentCollectionId,
        language = collection.Language, is_public = collection.IsPublic, original_collection_id = collection.OriginalCollectionId
    };
    internal static string Hash(List<Row> rows) => RunJson.Hash(RunJson.Write(rows));
    internal static async Task<List<Row>> SnapshotAsync(GlosifyContext db, string type, Guid id, string? user, CancellationToken ct, bool collectionOnly = false)
    {
        var entities = new List<object>();
        long? reviewCount = null;
        if (type == "anki")
        {
            var root = await db.AnkiCollections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.UserId == user, ct);
            if (root is null) return [];
            entities.Add(root);
            if (!collectionOnly)
            {
                entities.AddRange(await db.AnkiQuizLinks.AsNoTracking().Where(c => c.AnkiCollectionId == id).OrderBy(c => c.Id).ToListAsync(ct));
                entities.AddRange(await db.AnkiNotes.AsNoTracking().Where(c => c.AnkiCollectionId == id).OrderBy(c => c.Id).ToListAsync(ct));
                entities.AddRange(await db.AnkiCards.AsNoTracking().Where(c => c.Note.AnkiCollectionId == id).OrderBy(c => c.Id).ToListAsync(ct));
                // Reviews are append-only while a collection exists. Count is compact evidence
                // of intervening study; card snapshots also retain scheduling state.
                reviewCount = await db.AnkiReviews.LongCountAsync(c => c.AnkiCollectionId == id, ct);
            }
        }
        else if (type == "quiz")
        {
            var root = await db.Quizzes.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && (user == null || c.UserId == user), ct);
            if (root is null) return [];
            entities.Add(root);
            entities.AddRange(await db.Words.AsNoTracking().Where(c => c.QuizId == id).OrderBy(c => c.Id).ToListAsync(ct));
            entities.AddRange(await db.QuizSentences.AsNoTracking().Where(c => c.QuizId == id).OrderBy(c => c.Id).ToListAsync(ct));
        }
        else
        {
            var root = await db.Collections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && (user == null || c.UserId == user), ct);
            if (root is null) return [];
            entities.Add(root);
            var folderIds = new HashSet<Guid> { id };
            var frontier = new List<Guid> { id };
            while (frontier.Count > 0)
            {
                var children = await db.Collections.AsNoTracking().Where(c => c.UserId == root.UserId && c.ParentCollectionId.HasValue && frontier.Contains(c.ParentCollectionId.Value)).OrderBy(c => c.Id).ToListAsync(ct);
                frontier = children.Where(c => folderIds.Add(c.Id)).Select(c => c.Id).ToList();
                entities.AddRange(children.Where(c => frontier.Contains(c.Id)));
            }
            var contents = await db.Quizzes.AsNoTracking().Where(c => c.UserId == root.UserId && c.CollectionId.HasValue && folderIds.Contains(c.CollectionId.Value)).OrderBy(c => c.Id).ToListAsync(ct);
            entities.AddRange(contents);
            var quizIds = contents.Select(c => c.Id).ToList();
            entities.AddRange(await db.Words.AsNoTracking().Where(c => quizIds.Contains(c.QuizId)).OrderBy(c => c.Id).ToListAsync(ct));
            entities.AddRange(await db.QuizSentences.AsNoTracking().Where(c => quizIds.Contains(c.QuizId)).OrderBy(c => c.Id).ToListAsync(ct));
        }
        var rows = entities.Select(entity =>
        {
            var entry = db.Entry(entity);
            var values = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in entry.Properties.Where(p => !p.Metadata.IsShadowProperty() && p.Metadata.Name != "RowVersion"))
                values[property.Metadata.Name] = JsonSerializer.SerializeToElement(property.CurrentValue, property.Metadata.ClrType);
            return new Row(entity.GetType().Name, values["Id"].ToString(), values);
        }).ToList();
        if (reviewCount is long count)
            rows.Add(new Row("AnkiReviewCount", id.ToString(), new() { ["Count"] = JsonSerializer.SerializeToElement(count) }));
        return rows;
    }

    internal async Task<AppliedChange> ApplyAsync(PendingChange change, string user, CancellationToken ct)
    {
        var p = change.Payload;
        var type = p.GetProperty("entity_type").GetString()!;
        var id = p.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String ? idValue.GetGuid() : Guid.Empty;
        var copy = change.Kind.StartsWith("copy_", StringComparison.Ordinal);
        if (copy && !await IsPublicAsync(db, type, id, ct)) throw new InvalidOperationException("Public content is no longer available.");
        var collectionOnly = type == "anki" && (change.Kind is "anki_rename" or "anki_settings")
            && p.TryGetProperty("snapshot_scope", out var scope) && scope.GetString() == "collection";
        var before = await SnapshotAsync(db, type, id, copy ? null : user, ct, collectionOnly);
        if (Hash(before) != p.GetProperty("expected").GetString())
            throw new AssistantRunConflictException("This item changed. Read its current state and request the change again.");
        bool ok = true;
        if (type == "anki")
        {
            var a = JsonSerializer.Deserialize<ManageAnkiArgs>(p.GetProperty("args"), RunJson.Options)!;
            switch (a.Action)
            {
                case "create":
                    var created = await anki.CreateAsync(new(a.Name!, a.SourceLanguage ?? "English", a.TargetLanguage ?? "", a.TimeZoneId ?? "UTC"), user, ct);
                    id = created.Id; break;
                case "rename": ok = await anki.RenameAsync(id, a.Name!, user, ct); break;
                case "delete": ok = await anki.DeleteAsync(id, user, ct); break;
                case "direction": ok = await anki.SetDirectionAsync(id, a.Direction!, user, ct); break;
                case "settings":
                    var c = await db.AnkiCollections.SingleAsync(c => c.Id == id && c.UserId == user, ct);
                    ok = await anki.UpdateSettingsAsync(id, a.DesiredRetention ?? c.DesiredRetention, a.NewCardsPerDay ?? c.NewCardsPerDay,
                        a.MaximumReviewsPerDay ?? c.MaximumReviewsPerDay, a.TimeZoneId ?? c.TimeZoneId, user, ct); break;
                case "add_quiz": ok = await anki.AddQuizAsync(new(id, a.QuizId!.Value, a.WordsSourceToTarget ?? false, a.WordsTargetToSource ?? false, a.SentencesSourceToTarget ?? false, a.SentencesTargetToSource ?? false), user, ct); break;
                case "remove_quiz": ok = await anki.RemoveQuizAsync(id, a.QuizId!.Value, user, ct); break;
                case "add_item": ok = await anki.AddItemAsync(new(id, a.QuizId!.Value, a.ItemType ?? "words", a.ItemId ?? "", a.Direction == PracticeDirection.SourceToTarget, a.Direction == PracticeDirection.TargetToSource), user, ct); break;
                case "remove_card":
                    if (!await db.AnkiCards.AnyAsync(c => c.Id == a.CardId && c.Note.AnkiCollectionId == id && c.Note.Collection.UserId == user, ct)) ok = false;
                    else ok = await anki.RemoveCardAsync(a.CardId!.Value, user, ct);
                    break;
            }
        }
        else
        {
            switch (change.Kind)
            {
                case "rename_quiz":
                    var q = await db.Quizzes.SingleAsync(q => q.Id == id && q.UserId == user, ct);
                    q.Name = p.GetProperty("name").GetString()!.Trim(); await db.SaveChangesAsync(ct); break;
                case "delete_quiz": ok = await quizzes.DeleteQuizAsync(id, user, ct) is not null; break;
                case "delete_collection": ok = await collections.DeleteCollectionAsync(id, user, ct); break;
                case "quiz_visibility": ok = await quizzes.SetQuizPublicAsync(id, user, p.GetProperty("isPublic").GetBoolean(), ct); break;
                case "collection_visibility": ok = await collections.SetCollectionPublicAsync(id, user, p.GetProperty("isPublic").GetBoolean(), ct); break;
                case "copy_quiz":
                    var copied = await quizzes.CopyPublicQuizAsync(id, user, cancellationToken: ct);
                    ok = copied is not null; id = copied?.Id ?? id; break;
                case "copy_collection":
                    var copiedFolder = await collections.CopyPublicCollectionAsync(id, user, ct);
                    ok = copiedFolder is not null; id = copiedFolder?.Id ?? id; break;
            }
        }
        if (!ok) throw new InvalidOperationException("The change could not be applied. Check ownership, compatible languages, membership and child folders, then read the current state.");
        var after = await SnapshotAsync(db, type, id, user, ct, collectionOnly);
        return new AppliedChange(change.Kind, type, id.ToString(), type == "quiz" ? id : null,
            copy ? null : before, Permanent(change.Kind) ? null : after);
    }

    internal async Task<bool> UndoAsync(AssistantChange change, string user, CancellationToken ct)
    {
        if (Permanent(change.Kind) || change.AfterJson is null) return false;
        var id = Guid.Parse(change.EntityId);
        var after = RunJson.Read<List<Row>>(change.AfterJson);
        var collectionOnly = (change.Kind is "anki_rename" or "anki_settings") && after.Count == 1 && after[0].Type == nameof(AnkiCollection);
        var current = await SnapshotAsync(db, change.EntityType, id, user, ct, collectionOnly);
        if (current.Count == 0 || Hash(current) != Hash(after)) return false;
        var before = change.BeforeJson is null ? [] : RunJson.Read<List<Row>>(change.BeforeJson);
        if (before.Count == 0)
        {
            var createdQuizIds = current.Where(r => r.Type == nameof(Quiz)).Select(r => Guid.Parse(r.Id)).ToList();
            if (await db.AnkiNotes.AnyAsync(n => createdQuizIds.Contains(n.QuizId), ct)
                || await db.AnkiQuizLinks.AnyAsync(l => createdQuizIds.Contains(l.QuizId), ct)
                || await db.QuizAttempts.AnyAsync(a => createdQuizIds.Contains(a.QuizId), ct)) return false;
            if (change.EntityType == "anki") return await anki.DeleteAsync(id, user, ct);
            if (change.EntityType == "quiz") return await quizzes.DeleteQuizAsync(id, user, ct) is not null;
            foreach (var quiz in current.Where(r => r.Type == nameof(Quiz)))
                if (await quizzes.DeleteQuizAsync(Guid.Parse(quiz.Id), user, ct) is null) return false;
            foreach (var folder in current.Where(r => r.Type == nameof(Collection)).Reverse())
                if (!await collections.DeleteCollectionAsync(Guid.Parse(folder.Id), user, ct)) return false;
            return true;
        }
        // Restore only scalar rows in the validated owned aggregate. Reviews are never rewritten.
        foreach (var row in after.Where(r => !before.Any(b => b.Type == r.Type && b.Id == r.Id)).Reverse())
        {
            if (row.Type == "AnkiReviewCount") continue;
            if (row.Type == nameof(AnkiReview)) return false;
            var entity = await FindRowAsync(row, ct);
            if (entity is not null) db.Remove(entity);
        }
        foreach (var row in before)
        {
            if (row.Type is nameof(AnkiReview) or "AnkiReviewCount") continue;
            var entity = await FindRowAsync(row, ct);
            if (entity is null) { entity = Activator.CreateInstance(Types[row.Type])!; db.Add(entity); }
            var entry = db.Entry(entity);
            foreach (var (name, value) in row.Values)
            {
                var property = entry.Property(name);
                property.CurrentValue = value.Deserialize(property.Metadata.ClrType);
            }
        }
        await db.SaveChangesAsync(ct);
        return true;
    }
    private async Task<object?> FindRowAsync(Row row, CancellationToken ct) =>
        await db.FindAsync(Types[row.Type], [row.Type == nameof(Word) ? row.Id : Guid.Parse(row.Id)], ct);
}
