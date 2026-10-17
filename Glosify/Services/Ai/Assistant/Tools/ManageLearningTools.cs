using System.ComponentModel;
using System.Text.Json;
using Glosify.Data;
using Glosify.Models;
using Glosify.Services.Language;
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
    [property: Description("settings, direction, add_quiz, remove_quiz, add_item, or remove_card.")] string Action,
    Guid? CollectionId = null,
    string? TimeZoneId = null, string? Direction = null, Guid? QuizId = null,
    string? ItemType = null, string? ItemId = null, Guid? CardId = null,
    bool? WordsSourceToTarget = null, bool? WordsTargetToSource = null,
    bool? SentencesSourceToTarget = null, bool? SentencesTargetToSource = null);

internal sealed class ManageAnkiTool(GlosifyContext db) : AssistantTool<ManageAnkiArgs>
{
    public override string Name => "manage_anki";
    public override AssistantToolKind Kind => AssistantToolKind.Write;
    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Language;
    protected override string Describe(AssistantMode mode) => "Manage the Anki collection for the current learning language and its card membership. Read inspect_anki first. direction sets which direction the learner practices; each direction keeps its own schedule. source-to-target prompts in the translation language, target-to-source prompts in the learning language. add_quiz requires at least one of its four direction flags; add_item uses direction and item_type (word/words, sentence/sentences; omitted defaults to words). Removals require approval. settings only changes time_zone_id. Retention and daily card limits are shared defaults for everyone and cannot be customized.";
    protected override async Task<ToolResult> RunAsync(ManageAnkiArgs args, ToolContext context, CancellationToken ct)
    {
        var language = QuizLanguageCatalog.Find(context.TargetLanguage ?? "")?.Name;
        args = args with { CollectionId = args.CollectionId ?? context.AnkiCollectionId
            ?? await db.AnkiCollections.Where(c => c.UserId == context.UserId && c.TargetLanguage == language).Select(c => (Guid?)c.Id).SingleOrDefaultAsync(ct) };
        if (args.Action == "settings" && string.IsNullOrWhiteSpace(args.TimeZoneId))
            return ToolResult.Fail("Timezone required", "settings requires time_zone_id. Retention and daily limits use shared defaults for everyone.");
        if (!LearningChanges.AnkiActions.Contains(args.Action)) return ToolResult.Fail("Unknown action", "Use an action listed in the schema description.");
        if (args.Action is "direction" or "add_item" && args.Direction is not (PracticeDirection.SourceToTarget or PracticeDirection.TargetToSource))
            return ToolResult.Fail("Invalid direction", "Use source-to-target or target-to-source.");
        if (args.Action is "add_quiz" or "remove_quiz" or "add_item" && args.QuizId is null) return ToolResult.Fail("Quiz required", "Provide quiz_id from list_library.");
        if (args.Action == "remove_card" && (args.CardId is null || args.CardId == Guid.Empty))
            return ToolResult.Fail("Card required", "Provide card_id from inspect_anki.");
        if (args.Action == "add_quiz" && args.WordsSourceToTarget != true && args.WordsTargetToSource != true
            && args.SentencesSourceToTarget != true && args.SentencesTargetToSource != true)
            return ToolResult.Fail("Direction required", "Choose at least one content type and direction.");
        if (args.Action == "add_item")
        {
            var itemType = args.ItemType?.Trim().ToLowerInvariant() ?? PracticeItemType.Words;
            if (itemType is not ("word" or "words" or "sentence" or "sentences"))
                return ToolResult.Fail("Invalid item type", "Use words or sentences.");
            if (string.IsNullOrWhiteSpace(args.ItemId))
                return ToolResult.Fail("Item required", "Provide item_id from the selected quiz.");
            var sentence = itemType is "sentence" or "sentences";
            args = args with { ItemType = sentence ? PracticeItemType.Sentences : PracticeItemType.Words };
            var exists = sentence
                ? Guid.TryParse(args.ItemId, out var sentenceId) && await db.QuizSentences.AnyAsync(item => item.Id == sentenceId
                    && item.QuizId == args.QuizId && db.Quizzes.Any(quiz => quiz.Id == item.QuizId && quiz.UserId == context.UserId), ct)
                : await db.Words.AnyAsync(item => item.Id == args.ItemId && item.QuizId == args.QuizId
                    && db.Quizzes.Any(quiz => quiz.Id == item.QuizId && quiz.UserId == context.UserId), ct);
            if (!exists) return ToolResult.Fail("Item not found", "Read the owned quiz and provide an item_id matching its content type.");
        }
        if (args.Action == "remove_card" && !await db.AnkiCards.AnyAsync(card => card.Id == args.CardId
                && card.Note.AnkiCollectionId == args.CollectionId && card.Note.Collection.UserId == context.UserId, ct))
            return ToolResult.Fail("Card not found", "Use inspect_anki to find a card in this owned collection.");
        var collectionOnly = args.Action is "settings";
        var snapshot = await LearningChanges.SnapshotAsync(db, "anki", args.CollectionId ?? Guid.Empty, context.UserId, ct, collectionOnly);
        if (snapshot.Count == 0) return ToolResult.Fail("Collection not found",
            "This learning language has no Anki collection yet. add_anki_items and link_anki_quiz create it.");
        var title = $"Anki {args.Action.Replace('_', ' ')}";
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
    protected override string Describe(AssistantMode mode) => "List the user's Anki collections (one per learning language; cards from any translation language), or inspect a collection's settings, linked quizzes, cards and review statistics. Cards are paged in batches of 100; use offset for more. Set list_all to list collections even when one is selected. These are study collections, not library folders.";
    protected override async Task<ToolResult> RunAsync(InspectAnkiArgs args, ToolContext context, CancellationToken ct)
    {
        args = args with { CollectionId = args.ListAll ? null : args.CollectionId ?? context.AnkiCollectionId };
        var collections = db.AnkiCollections.AsNoTracking().Where(c => c.UserId == context.UserId);
        if (args.CollectionId is not Guid id)
            return ToolResult.Ok("Anki collections", await collections.Select(c => new { c.Id, c.TargetLanguage, c.DefaultDirection }).ToListAsync(ct));
        var collection = await collections.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (collection is null) return ToolResult.Fail("Not found", "Anki collection not found.");
        var cards = db.AnkiCards.AsNoTracking().Where(c => c.Note.AnkiCollectionId == id);
        var now = clock.GetUtcNow();
        var dayStart = Glosify.Services.Anki.AnkiCollectionService.StartOfCollectionDay(collection.TimeZoneId, now);
        var reviews = await ReviewSummaryAsync(db, id, dayStart, now.AddDays(-30), ct);
        var active = await CardSummaryAsync(db, id, now, ct);
        return ToolResult.Ok("Anki collection details", new
        {
            collection.Id, collection.TargetLanguage, collection.DefaultDirection,
            AnkiStudyDefaults.DesiredRetention, AnkiStudyDefaults.NewCardsPerDay, AnkiStudyDefaults.MaximumReviewsPerDay, collection.TimeZoneId,
            total_cards = await cards.CountAsync(ct), active_cards = active.Active, total_reviews = reviews.Total,
            due_cards = active.Due,
            new_cards = active.New,
            learning_cards = active.Learning,
            studied_today = reviews.Today,
            reviews_last_30_days = reviews.Recent,
            retention_percent_last_30_days = reviews.Recent == 0 ? 0 : 100d * reviews.Passed / reviews.Recent,
            links = await db.AnkiQuizLinks.AsNoTracking().Where(l => l.AnkiCollectionId == id).Select(l => new { l.QuizId, l.WordsSourceToTarget, l.WordsTargetToSource, l.SentencesSourceToTarget, l.SentencesTargetToSource }).ToListAsync(ct),
            cards = await cards.OrderBy(c => c.Id).Skip(Math.Max(0, args.Offset)).Take(100).Select(c => new
            { c.Id, c.Direction, c.State, c.IsActive, c.DueAt, c.DirectlyIncluded, c.ExcludedFromQuizLink, c.Note.QuizId, c.Note.ItemType, c.Note.WordId, c.Note.SentenceId, c.Note.TargetText, c.Note.SourceText }).ToListAsync(ct)
        });
    }

    internal sealed class CardSummary
    {
        public long Active { get; set; }
        public long Due { get; set; }
        public long New { get; set; }
        public long Learning { get; set; }
    }

    private static async Task<CardSummary> CardSummaryAsync(GlosifyContext db, Guid id, DateTimeOffset now, CancellationToken ct)
    {
        if (db.Database.ProviderName?.Contains("Sqlite", StringComparison.Ordinal) == true
            && db.Model.FindEntityType(typeof(Glosify.Models.Entities.AnkiCard))!
                .FindProperty(nameof(Glosify.Models.Entities.AnkiCard.DueAt))!.GetTypeMapping().Converter is null)
        {
            return await db.Database.SqlQuery<CardSummary>($"""
                SELECT COUNT(*) AS Active,
                    COALESCE(SUM(CASE WHEN c.State <> 'new' AND julianday(c.DueAt) <= julianday({now}) THEN 1 ELSE 0 END), 0) AS Due,
                    COALESCE(SUM(CASE WHEN c.State = 'new' THEN 1 ELSE 0 END), 0) AS New,
                    COALESCE(SUM(CASE WHEN c.State IN ('learning', 'relearning') THEN 1 ELSE 0 END), 0) AS Learning
                FROM AnkiCards c INNER JOIN AnkiNotes n ON n.Id = c.AnkiNoteId
                WHERE n.AnkiCollectionId = {id} AND c.IsActive = 1
                """).SingleAsync(ct);
        }
        var active = db.AnkiCards.Where(c => c.Note.AnkiCollectionId == id && c.IsActive);
        return new CardSummary
        {
            Active = await active.LongCountAsync(ct),
            Due = await active.LongCountAsync(c => c.State != "new" && c.DueAt <= now, ct),
            New = await active.LongCountAsync(c => c.State == "new", ct),
            Learning = await active.LongCountAsync(c => c.State == "learning" || c.State == "relearning", ct),
        };
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
    protected override string Describe(AssistantMode mode) => "Return a practice link for an owned quiz or Anki collection in the current app language. Mode: flashcards, typing, or anki. Include the returned link in your reply. Does not submit answers for the learner.";
    protected override async Task<ToolResult> RunAsync(OpenPracticeArgs args, ToolContext context, CancellationToken ct)
    {
        if (args.Mode is not ("flashcards" or "typing" or "anki") || args.Direction is not (PracticeDirection.SourceToTarget or PracticeDirection.TargetToSource)
            || args.ItemType is not ("words" or "sentences") || args.Count < 1 || args.Count > QuizSessionLimits.MaxItems)
            return ToolResult.Fail("Invalid settings", "Choose flashcards/typing/anki, source-to-target/target-to-source, words/sentences and a count from 1 to 1000.");
        var target = args.Mode == "anki"
            ? await db.AnkiCollections.Where(c => c.Id == args.Id && c.UserId == context.UserId).Select(c => c.TargetLanguage).SingleOrDefaultAsync(ct)
            : await db.Quizzes.Where(q => q.Id == args.Id && q.UserId == context.UserId).Select(q => q.TargetLanguage).SingleOrDefaultAsync(ct);
        if (target is null) return ToolResult.Fail("Not found", "Read the library to choose an owned quiz or collection.");
        // Selected reading material may use another language, and a queued run may outlive
        // a preference change. Practice routes use the current app selection instead.
        var selected = await db.Users.Where(user => user.Id == context.UserId)
            .Select(user => user.SelectedQuizLanguageCode).SingleOrDefaultAsync(ct);
        var language = QuizLanguageCatalog.Find(selected);
        if (language is null && args.Mode == "anki")
            return ToolResult.Fail("Choose a language", "Select a learning language before opening Anki practice.");
        if (language is not null && QuizLanguageCatalog.Find(target)?.Code != language.Code)
            return ToolResult.Fail("Different learning language", $"Switch the app's learning language to {target} before opening this practice.");
        var url = args.Mode == "anki" ? "/Anki" : $"/Quiz/{(args.Mode == "typing" ? "Type" : "Flashcard")}?id={args.Id}&wordCount={args.Count}&practiceDirection={args.Direction}&practiceItemType={args.ItemType}";
        return ToolResult.Ok("Open practice", new { url });
    }
}
