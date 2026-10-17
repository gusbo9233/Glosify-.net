using Glosify.Models;
using System.ComponentModel;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Anki;
using Glosify.Services.Language;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant.Tools;

internal enum AnkiDirection { Both, SourceToTarget, TargetToSource }
internal enum AnkiContent { Words, Sentences, Both }
internal sealed record ListAnkiArgs([property: Description("Rows to skip. Null for 0.")] int? Offset = null);
internal sealed record GetAnkiArgs(
    [property: Description("Anki collection id from list_anki_collections, not a quiz-library collection id.")] string CollectionId,
    int? CardOffset = null, int? LinkOffset = null);
internal sealed record AddAnkiArgs(
    QuizItemKind Kind,
    [property: Description("Exact ids from list_items or search_items. At most 100 per call. For first N use list_items with order=created first.")] IReadOnlyList<string> ItemIds,
    string? QuizId = null,
    [property: Description("Null means both directions, each scheduled separately.")] AnkiDirection? Direction = null,
    [property: Description("Optional. Null uses the collection for the quiz's learning language, created if missing.")] string? CollectionId = null);
internal sealed record LinkAnkiArgs(
    [property: Description("Content to synchronize, including future quiz additions. Null means words and sentences.")] AnkiContent? Content = null,
    string? QuizId = null,
    [property: Description("Null means both directions, each scheduled separately.")] AnkiDirection? Direction = null,
    [property: Description("Optional. Null uses the collection for the quiz's learning language, created if missing.")] string? CollectionId = null);

internal sealed class ListAnkiCollectionsTool(IAnkiCollectionService anki) : AssistantTool<ListAnkiArgs>
{
    public override string Name => "list_anki_collections";
    public override AssistantToolKind Kind => AssistantToolKind.Read;
    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Language;
    protected override string Describe(AssistantMode mode) => "List the user's built-in Anki collections (one per learning language, holding cards from any translation language), practiced direction and due/new card counts. These are separate from quiz-library collections. Follow next_offset to see more.";
    protected override async Task<ToolResult> RunAsync(ListAnkiArgs args, ToolContext context, CancellationToken ct)
    {
        var page = await anki.BrowseAsync(context.UserId, args.Offset ?? 0, ct);
        return ToolResult.Ok("Listed Anki collections", new { collections = page.Items, total_count = page.TotalCount, next_offset = page.NextOffset });
    }
}

internal sealed class GetAnkiCollectionTool(IAnkiCollectionService anki) : AssistantTool<GetAnkiArgs>
{
    public override string Name => "get_anki_collection";
    public override AssistantToolKind Kind => AssistantToolKind.Read;
    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Language;
    protected override string Describe(AssistantMode mode) => "Inspect an owned Anki collection's cards and synchronized quiz links. Each list is paginated separately, with next_offset. Reading does not change the collection.";
    protected override async Task<ToolResult> RunAsync(GetAnkiArgs args, ToolContext context, CancellationToken ct)
    {
        var details = Guid.TryParse(args.CollectionId, out var id) ? await anki.InspectAsync(id, context.UserId, args.CardOffset ?? 0, args.LinkOffset ?? 0, ct) : null;
        return details is null ? ToolResult.Fail("Could not read Anki collection", "Collection not found. Use list_anki_collections.")
            : ToolResult.Ok($"Read Anki collection “{details.Collection.Name}”", new
            {
                collection = details.Collection,
                cards = new { items = details.Cards.Items, total_count = details.Cards.TotalCount, next_offset = details.Cards.NextOffset },
                links = new { items = details.Links.Items, total_count = details.Links.TotalCount, next_offset = details.Links.NextOffset },
                url = AnkiTools.Url(id),
            });
    }
}

internal sealed class AddAnkiItemsTool(GlosifyContext db) : AssistantTool<AddAnkiArgs>
{
    public override string Name => "add_anki_items";
    public override AssistantToolKind Kind => AssistantToolKind.Write;
    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Language;
    protected override string Describe(AssistantMode mode) => "Add selected existing quiz words or sentences to the user's Anki collection for the quiz's learning language (created if missing). Does not link the whole quiz. Saved immediately with Undo; duplicates do not reset study progress. Null direction adds both directions. At most 100 ids per call.";
    protected override async Task<ToolResult> RunAsync(AddAnkiArgs args, ToolContext context, CancellationToken ct)
    {
        var target = await QuizContent.ResolveAsync(db, context, args.QuizId, ct);
        if (target.Quiz is not { } quiz) return ToolResult.Fail("Could not add Anki cards", target.Error!);
        var collection = await AnkiTools.ResolveAsync(db, args.CollectionId, quiz, context.UserId, ct);
        if (collection is { Found: false }) return ToolResult.Fail("Could not add Anki cards", "Anki collection not found. Omit collection_id to use the quiz's learning language.");
        if (args.ItemIds.Count is < 1 or > 100 || args.ItemIds.Any(string.IsNullOrWhiteSpace))
            return ToolResult.Fail("Could not add Anki cards", "Provide 1 to 100 item ids from list_items or search_items.");
        var (forward, reverse) = AnkiTools.Directions(args.Direction);
        return ToolResult.Propose($"Add {args.ItemIds.Distinct().Count()} selected items to Anki", quiz.Id,
            [QuizContent.Change(PendingChangeKinds.AddAnkiItems, new AddAnkiItemsInput(collection.Id, quiz.Id,
                args.Kind == QuizItemKind.Words ? PracticeItemType.Words : PracticeItemType.Sentences, args.ItemIds, forward, reverse))]);
    }
}

internal sealed class LinkAnkiQuizTool(GlosifyContext db) : AssistantTool<LinkAnkiArgs>
{
    public override string Name => "link_anki_quiz";
    public override AssistantToolKind Kind => AssistantToolKind.Write;
    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Language;
    protected override string Describe(AssistantMode mode) => "Link a whole quiz to the user's Anki collection for its learning language (created if missing), keeping future quiz additions synchronized. Only use when the user wants a whole-quiz link, not first N or selected items. Adds directions without removing existing links, exclusions or study progress. Saved with Undo.";
    protected override async Task<ToolResult> RunAsync(LinkAnkiArgs args, ToolContext context, CancellationToken ct)
    {
        var target = await QuizContent.ResolveAsync(db, context, args.QuizId, ct);
        if (target.Quiz is not { } quiz) return ToolResult.Fail("Could not link quiz to Anki", target.Error!);
        var collection = await AnkiTools.ResolveAsync(db, args.CollectionId, quiz, context.UserId, ct);
        if (collection is { Found: false }) return ToolResult.Fail("Could not link quiz to Anki", "Anki collection not found. Omit collection_id to use the quiz's learning language.");
        var (forward, reverse) = AnkiTools.Directions(args.Direction);
        var words = args.Content is null or AnkiContent.Both or AnkiContent.Words;
        var sentences = args.Content is null or AnkiContent.Both or AnkiContent.Sentences;
        return ToolResult.Propose($"Add “{quiz.Name}” to Anki, including future additions", quiz.Id,
            [QuizContent.Change(PendingChangeKinds.LinkAnkiQuiz, new AddAnkiQuizInput(collection.Id, quiz.Id,
                words && forward, words && reverse, sentences && forward, sentences && reverse))]);
    }
}

internal static class AnkiTools
{
    public static string Url(Guid id) => "/Anki";

    public static Task<AnkiCollection?> OwnedAsync(GlosifyContext db, string id, string userId, CancellationToken ct) =>
        Guid.TryParse(id, out var parsed) ? db.AnkiCollections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == parsed && c.UserId == userId, ct) : Task.FromResult<AnkiCollection?>(null);

    // Guid.Empty means "the collection for the quiz's learning language"; the change
    // applier creates it when it does not exist yet.
    public static async Task<(Guid Id, bool Found)> ResolveAsync(GlosifyContext db, string? collectionId, Quiz quiz, string userId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(collectionId))
            return await OwnedAsync(db, collectionId, userId, ct) is { } owned ? (owned.Id, true) : (Guid.Empty, false);
        var language = QuizLanguageCatalog.TargetName(quiz.TargetLanguage, quiz.Language);
        var existing = await db.AnkiCollections.AsNoTracking()
            .Where(c => c.UserId == userId && c.TargetLanguage == language).Select(c => (Guid?)c.Id).SingleOrDefaultAsync(ct);
        return (existing ?? Guid.Empty, true);
    }

    public static (bool Forward, bool Reverse) Directions(AnkiDirection? direction) => direction switch
    {
        AnkiDirection.SourceToTarget => (true, false),
        AnkiDirection.TargetToSource => (false, true),
        _ => (true, true),
    };
}
