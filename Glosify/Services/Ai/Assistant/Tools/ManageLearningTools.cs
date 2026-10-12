using System.ComponentModel;
using System.Text.Json;
using Glosify.Data;
using Glosify.Models;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed record ManageQuizArgs(
    [property: Description("rename_quiz, delete_quiz, delete_collection, quiz_visibility, collection_visibility, copy_quiz, or copy_collection.")] string Action,
    [property: Description("Quiz or library folder id from list_library or browse_public_library.")] Guid Id,
    string? Name = null, bool? IsPublic = null);

internal sealed class ManageQuizTool(GlosifyContext db) : AssistantTool<ManageQuizArgs>
{
    public override string Name => "manage_quiz_library";
    public override AssistantToolKind Kind => AssistantToolKind.Write;
    protected override string Describe(AssistantMode mode) => "Rename/delete quizzes, delete library folders, change visibility, or copy public content. Deletion is permanent and requires approval. Deleting a folder keeps quizzes at the root; move/remove child folders first. Use ids from library tools, never guess.";
    protected override async Task<ToolResult> RunAsync(ManageQuizArgs args, ToolContext context, CancellationToken ct)
    {
        if (!LearningChanges.QuizActions.Contains(args.Action)) return ToolResult.Fail("Unknown action", "Use an action listed in the schema description.");
        if (args.Action == "rename_quiz" && (string.IsNullOrWhiteSpace(args.Name) || args.Name.Trim().Length > 200))
            return ToolResult.Fail("Invalid name", "Provide a name between 1 and 200 characters.");
        if (args.Action.EndsWith("visibility", StringComparison.Ordinal) && args.IsPublic is null)
            return ToolResult.Fail("Missing visibility", "is_public is required.");
        var type = args.Action.Contains("collection", StringComparison.Ordinal) ? "collection" : "quiz";
        var copy = args.Action.StartsWith("copy_", StringComparison.Ordinal);
        if (copy && !await LearningChanges.IsPublicAsync(db, type, args.Id, ct))
            return ToolResult.Fail("Not found", "Public content not found. Use browse_public_library.");
        var snapshot = await LearningChanges.SnapshotAsync(db, type, args.Id, copy ? null : context.UserId, ct);
        if (snapshot.Count == 0) return ToolResult.Fail("Not found", "The target was not found. Read the library again.");
        var name = snapshot[0].Values.GetValueOrDefault("Name").GetString() ?? type;
        var title = $"{args.Action.Replace('_', ' ')}: {name}";
        if (args.Action.StartsWith("delete_", StringComparison.Ordinal)) title += " (permanent; cannot Undo)";
        if (args.Action == "delete_quiz") title += "; linked Anki cards will be retired";
        return ToolResult.Propose(title, null, [QuizContent.Change(args.Action, new
        {
            args.Action, args.Id, args.Name, args.IsPublic, entity_type = type,
            expected = LearningChanges.Hash(snapshot), title
        })]);
    }
}

internal sealed record ManageAnkiArgs(
    [property: Description("create, rename, delete, settings, direction, add_quiz, remove_quiz, add_item, or remove_card.")] string Action,
    Guid? CollectionId = null, string? Name = null, string? SourceLanguage = null, string? TargetLanguage = null,
    string? TimeZoneId = null, string? Direction = null, double? DesiredRetention = null,
    int? NewCardsPerDay = null, int? MaximumReviewsPerDay = null, Guid? QuizId = null,
    string? ItemType = null, string? ItemId = null, Guid? CardId = null,
    bool? WordsSourceToTarget = null, bool? WordsTargetToSource = null,
    bool? SentencesSourceToTarget = null, bool? SentencesTargetToSource = null);

internal sealed class ManageAnkiTool(GlosifyContext db) : AssistantTool<ManageAnkiArgs>
{
    public override string Name => "manage_anki";
    public override AssistantToolKind Kind => AssistantToolKind.Write;
    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Language;
    protected override string Describe(AssistantMode mode) => "Manage Anki study collections and card membership. Read inspect_anki first. direction replaces active directions, retaining inactive review history; source-to-target prompts in the translation language, target-to-source prompts in the learning language. add_quiz uses all four direction flags; add_item uses direction. Deletion is permanent. Removals/deletion require approval. settings changes only supplied values.";
    protected override async Task<ToolResult> RunAsync(ManageAnkiArgs args, ToolContext context, CancellationToken ct)
    {
        args = args with { CollectionId = args.Action == "create" ? null : args.CollectionId ?? context.AnkiCollectionId,
            SourceLanguage = args.SourceLanguage ?? context.SourceLanguage, TargetLanguage = args.TargetLanguage ?? context.TargetLanguage };
        if (args.DesiredRetention is < .70 or > .97 || args.NewCardsPerDay is < 0 or > 999 || args.MaximumReviewsPerDay is < 1 or > 9999)
            return ToolResult.Fail("Invalid settings", "Retention must be 0.70–0.97, new cards/day 0–999, reviews/day 1–9999.");
        if (!LearningChanges.AnkiActions.Contains(args.Action)) return ToolResult.Fail("Unknown action", "Use an action listed in the schema description.");
        if (args.Action is "direction" or "add_item" && args.Direction is not (PracticeDirection.SourceToTarget or PracticeDirection.TargetToSource))
            return ToolResult.Fail("Invalid direction", "Use source-to-target or target-to-source.");
        if (args.Action is "create" or "rename" && string.IsNullOrWhiteSpace(args.Name)) return ToolResult.Fail("Name required", "Provide a collection name.");
        if (args.Action is "add_quiz" or "remove_quiz" or "add_item" && args.QuizId is null) return ToolResult.Fail("Quiz required", "Provide quiz_id from list_library.");
        var collectionOnly = args.Action is "rename" or "settings";
        var snapshot = args.Action == "create" ? [] : await LearningChanges.SnapshotAsync(db, "anki", args.CollectionId ?? Guid.Empty, context.UserId, ct, collectionOnly);
        if (args.Action != "create" && snapshot.Count == 0) return ToolResult.Fail("Collection not found", "Use inspect_anki to find an owned collection.");
        var title = $"Anki {args.Action.Replace('_', ' ')}: {args.Name ?? (snapshot.Count > 0 ? snapshot[0].Values["Name"].GetString() : "collection")}";
        if (args.Action == "delete") title += " (permanent; deletes scheduling history; cannot Undo)";
        return ToolResult.Propose(title, null, [QuizContent.Change("anki_" + args.Action, new
        {
            args, expected = LearningChanges.Hash(snapshot), title, entity_type = "anki", id = args.CollectionId,
            snapshot_scope = collectionOnly ? "collection" : "aggregate"
        })]);
    }
}

internal sealed record InspectAnkiArgs(Guid? CollectionId = null, int Offset = 0, bool ListAll = false);
internal sealed class InspectAnkiTool(GlosifyContext db, TimeProvider clock) : AssistantTool<InspectAnkiArgs>
{
    public override string Name => "inspect_anki";
    public override AssistantToolKind Kind => AssistantToolKind.Read;
    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Language;
    protected override string Describe(AssistantMode mode) => "List owned Anki study collections, or inspect a collection's settings, linked quizzes, cards and review statistics. Cards are paged in batches of 100; use offset for more. Set list_all to list collections even when one is selected. These are study collections, not library folders.";
    protected override async Task<ToolResult> RunAsync(InspectAnkiArgs args, ToolContext context, CancellationToken ct)
    {
        args = args with { CollectionId = args.ListAll ? null : args.CollectionId ?? context.AnkiCollectionId };
        var collections = db.AnkiCollections.AsNoTracking().Where(c => c.UserId == context.UserId);
        if (args.CollectionId is not Guid id)
            return ToolResult.Ok("Anki collections", await collections.Select(c => new { c.Id, c.Name, c.SourceLanguage, c.TargetLanguage, c.DefaultDirection }).ToListAsync(ct));
        var collection = await collections.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (collection is null) return ToolResult.Fail("Not found", "Anki collection not found.");
        var cards = db.AnkiCards.AsNoTracking().Where(c => c.Note.AnkiCollectionId == id);
        var now = clock.GetUtcNow();
        var dayStart = Glosify.Services.Anki.AnkiCollectionService.StartOfCollectionDay(collection.TimeZoneId, now);
        var reviews = await ReviewSummaryAsync(db, id, dayStart, now.AddDays(-30), ct);
        var active = await cards.Where(c => c.IsActive).Select(c => new { c.State, c.DueAt }).ToListAsync(ct);
        return ToolResult.Ok("Anki collection details", new
        {
            collection.Id, collection.Name, collection.SourceLanguage, collection.TargetLanguage, collection.DefaultDirection,
            collection.DesiredRetention, collection.NewCardsPerDay, collection.MaximumReviewsPerDay, collection.TimeZoneId,
            total_cards = await cards.CountAsync(ct), active_cards = active.Count, total_reviews = reviews.Total,
            due_cards = active.Count(c => c.State != "new" && c.DueAt <= now),
            new_cards = active.Count(c => c.State == "new"),
            learning_cards = active.Count(c => c.State is "learning" or "relearning"),
            studied_today = reviews.Today,
            reviews_last_30_days = reviews.Recent,
            retention_percent_last_30_days = reviews.Recent == 0 ? 0 : 100d * reviews.Passed / reviews.Recent,
            links = await db.AnkiQuizLinks.AsNoTracking().Where(l => l.AnkiCollectionId == id).Select(l => new { l.QuizId, l.WordsSourceToTarget, l.WordsTargetToSource, l.SentencesSourceToTarget, l.SentencesTargetToSource }).ToListAsync(ct),
            cards = await cards.OrderBy(c => c.Id).Skip(Math.Max(0, args.Offset)).Take(100).Select(c => new
            { c.Id, c.Direction, c.State, c.IsActive, c.DueAt, c.DirectlyIncluded, c.ExcludedFromQuizLink, c.Note.QuizId, c.Note.ItemType, c.Note.WordId, c.Note.SentenceId, c.Note.TargetText, c.Note.SourceText }).ToListAsync(ct)
        });
    }

    internal sealed class ReviewSummary
    {
        public long Total { get; set; }
        public long Today { get; set; }
        public long Recent { get; set; }
        public long Passed { get; set; }
    }

    private static async Task<ReviewSummary> ReviewSummaryAsync(GlosifyContext db, Guid id,
        DateTimeOffset dayStart, DateTimeOffset periodStart, CancellationToken ct)
    {
        if (db.Database.ProviderName?.Contains("Sqlite", StringComparison.Ordinal) == true
            && db.Model.FindEntityType(typeof(Glosify.Models.Entities.AnkiReview))!
                .FindProperty(nameof(Glosify.Models.Entities.AnkiReview.ReviewedAt))!.GetTypeMapping().Converter is null)
        {
            // SQLite cannot translate DateTimeOffset comparisons; aggregate in SQL using
            // its offset-aware date functions instead of loading lifetime history in tests.
            return await db.Database.SqlQuery<ReviewSummary>($"""
                SELECT COUNT(*) AS Total,
                    COALESCE(SUM(CASE WHEN julianday(ReviewedAt) >= julianday({dayStart}) THEN 1 ELSE 0 END), 0) AS Today,
                    COALESCE(SUM(CASE WHEN julianday(ReviewedAt) >= julianday({periodStart}) THEN 1 ELSE 0 END), 0) AS Recent,
                    COALESCE(SUM(CASE WHEN julianday(ReviewedAt) >= julianday({periodStart}) AND Rating <> 'again' THEN 1 ELSE 0 END), 0) AS Passed
                FROM AnkiReviews WHERE AnkiCollectionId = {id}
                """).SingleAsync(ct);
        }
        var query = db.AnkiReviews.Where(r => r.AnkiCollectionId == id);
        return new ReviewSummary
        {
            Total = await query.LongCountAsync(ct),
            Today = await query.LongCountAsync(r => r.ReviewedAt >= dayStart, ct),
            Recent = await query.LongCountAsync(r => r.ReviewedAt >= periodStart, ct),
            Passed = await query.LongCountAsync(r => r.ReviewedAt >= periodStart && r.Rating != "again", ct),
        };
    }
}
internal sealed record PublicLibraryArgs(string? Language = null);
internal sealed class BrowsePublicLibraryTool(GlosifyContext db) : AssistantTool<PublicLibraryArgs>
{
    public override string Name => "browse_public_library";
    public override AssistantToolKind Kind => AssistantToolKind.Read;
    protected override string Describe(AssistantMode mode) => "Find public quizzes and library collections to copy. Returns ids and names for the selected learning language.";
    protected override async Task<ToolResult> RunAsync(PublicLibraryArgs args, ToolContext context, CancellationToken ct)
    {
        var language = context.IsFreestyle ? "Freestyle" : args.Language ?? context.TargetLanguage;
        return ToolResult.Ok("Public library", new {
            quizzes = await db.Quizzes.AsNoTracking().Where(q => q.IsPublic && q.TargetLanguage == language).OrderBy(q => q.Name).Take(100).Select(q => new { q.Id, q.Name }).ToListAsync(ct),
            collections = await db.Collections.AsNoTracking().Where(c => c.IsPublic && c.Language == language).OrderBy(c => c.Name).Take(100).Select(c => new { c.Id, c.Name }).ToListAsync(ct)
        });
    }
}

internal sealed record OpenPracticeArgs(Guid Id, string Mode = "flashcards", string Direction = "source-to-target", string ItemType = "words", int Count = 20);
internal sealed class OpenPracticeTool(GlosifyContext db) : AssistantTool<OpenPracticeArgs>
{
    public override string Name => "open_practice";
    public override AssistantToolKind Kind => AssistantToolKind.Read;
    protected override string Describe(AssistantMode mode) => "Return a practice link for an owned quiz or Anki collection. Mode: flashcards, typing, or anki. Include the returned link in your reply. Does not submit answers for the learner.";
    protected override async Task<ToolResult> RunAsync(OpenPracticeArgs args, ToolContext context, CancellationToken ct)
    {
        if (args.Mode is not ("flashcards" or "typing" or "anki") || args.Direction is not (PracticeDirection.SourceToTarget or PracticeDirection.TargetToSource)
            || args.ItemType is not ("words" or "sentences") || args.Count < 1 || args.Count > QuizSessionLimits.MaxItems)
            return ToolResult.Fail("Invalid settings", "Choose flashcards/typing/anki, source-to-target/target-to-source, words/sentences and a count from 1 to 1000.");
        var owned = args.Mode == "anki" ? await db.AnkiCollections.AnyAsync(c => c.Id == args.Id && c.UserId == context.UserId, ct)
            : await db.Quizzes.AnyAsync(q => q.Id == args.Id && q.UserId == context.UserId, ct);
        if (!owned) return ToolResult.Fail("Not found", "Read the library to choose an owned quiz or collection.");
        var url = args.Mode == "anki" ? $"/Anki/Study?id={args.Id}" : $"/Quiz/{(args.Mode == "typing" ? "Type" : "Flashcard")}?id={args.Id}&wordCount={args.Count}&practiceDirection={args.Direction}&practiceItemType={args.ItemType}";
        return ToolResult.Ok("Open practice", new { url });
    }
}
