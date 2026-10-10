using Glosify.Models;
using System.ComponentModel;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Anki;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant.Tools;

internal enum AnkiDirection { CollectionDefault, SourceToTarget, TargetToSource, Both }
internal enum AnkiContent { Words, Sentences, Both }
internal sealed record ListAnkiArgs([property: Description("Rows to skip. Null for 0.")] int? Offset = null);
internal sealed record GetAnkiArgs(
    [property: Description("Anki collection id from list_anki_collections, not a quiz-library collection id.")] string CollectionId,
    int? CardOffset = null, int? LinkOffset = null);
internal sealed record CreateAnkiArgs(
    [property: Description("Name requested by the user for the new Anki study collection.")] string Name,
    [property: Description("Quiz whose language pair to use. Null for this chat's quiz. Creates an empty collection, not a whole-quiz link.")] string? QuizId = null);
internal sealed record AddAnkiArgs(
    string CollectionId, QuizItemKind Kind,
    [property: Description("Exact ids from list_items or search_items. At most 100 per call. For first N use list_items with order=created first.")] IReadOnlyList<string> ItemIds,
    string? QuizId = null, AnkiDirection? Direction = null);
internal sealed record LinkAnkiArgs(string CollectionId,
    [property: Description("Content to synchronize, including future quiz additions. Null means words and sentences.")] AnkiContent? Content = null,
    string? QuizId = null, AnkiDirection? Direction = null);

internal sealed class ListAnkiCollectionsTool(IAnkiCollectionService anki) : AssistantTool<ListAnkiArgs>
{
    public override string Name => "list_anki_collections";
    public override AssistantToolKind Kind => AssistantToolKind.Read;
    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Language;
    protected override string Describe(AssistantMode mode) => "List the user's built-in Anki study collections, language pairs, default directions and due/new card counts. These are separate from quiz-library collections. Follow next_offset to see more.";
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

internal sealed class CreateAnkiCollectionTool(GlosifyContext db) : AssistantTool<CreateAnkiArgs>
{
    public override string Name => "create_anki_collection";
    public override AssistantToolKind Kind => AssistantToolKind.Write;
    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Language;
    protected override string Describe(AssistantMode mode) => "Create an empty built-in Anki study collection using a quiz's language pair. Saved immediately with Undo. Then use add_anki_items for a selection or link_anki_quiz for ongoing whole-quiz synchronization.";
    protected override async Task<ToolResult> RunAsync(CreateAnkiArgs args, ToolContext context, CancellationToken ct)
    {
        var target = await QuizContent.ResolveAsync(db, context, args.QuizId, ct);
        if (target.Quiz is not { } quiz) return ToolResult.Fail("Could not create Anki collection", target.Error!);
        var name = args.Name.Trim();
        if (name.Length is < 1 or > 160) return ToolResult.Fail("Could not create Anki collection", "Use a collection name between 1 and 160 characters.");
        if (await db.AnkiCollections.AnyAsync(c => c.UserId == context.UserId && c.Name == name, ct))
            return ToolResult.Fail("Could not create Anki collection", "An Anki collection with that name already exists. Use list_anki_collections to find it.");
        return ToolResult.Propose($"Create Anki collection “{name}”", quiz.Id,
            [QuizContent.Change(PendingChangeKinds.CreateAnkiCollection, new { name, quiz_id = quiz.Id })]);
    }
}

internal sealed class AddAnkiItemsTool(GlosifyContext db) : AssistantTool<AddAnkiArgs>
{
    public override string Name => "add_anki_items";
    public override AssistantToolKind Kind => AssistantToolKind.Write;
    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Language;
    protected override string Describe(AssistantMode mode) => "Add selected existing quiz words or sentences to a built-in Anki collection. Does not link the whole quiz. Saved immediately with Undo; duplicates do not reset study progress. Null direction uses the collection default; both creates two cards per item. At most 100 ids per call.";
    protected override async Task<ToolResult> RunAsync(AddAnkiArgs args, ToolContext context, CancellationToken ct)
    {
        var target = await QuizContent.ResolveAsync(db, context, args.QuizId, ct);
        if (target.Quiz is not { } quiz) return ToolResult.Fail("Could not add Anki cards", target.Error!);
        var collection = await AnkiTools.OwnedAsync(db, args.CollectionId, context.UserId, ct);
        if (collection is null) return ToolResult.Fail("Could not add Anki cards", "Anki collection not found. Use list_anki_collections.");
        if (args.ItemIds.Count is < 1 or > 100 || args.ItemIds.Any(string.IsNullOrWhiteSpace))
            return ToolResult.Fail("Could not add Anki cards", "Provide 1 to 100 item ids from list_items or search_items.");
        var (forward, reverse) = AnkiTools.Directions(args.Direction, collection);
        return ToolResult.Propose($"Add {args.ItemIds.Distinct().Count()} selected items to Anki collection “{collection.Name}”", quiz.Id,
            [QuizContent.Change(PendingChangeKinds.AddAnkiItems, new AddAnkiItemsInput(collection.Id, quiz.Id,
                args.Kind == QuizItemKind.Words ? PracticeItemType.Words : PracticeItemType.Sentences, args.ItemIds, forward, reverse))]);
    }
}

internal sealed class LinkAnkiQuizTool(GlosifyContext db) : AssistantTool<LinkAnkiArgs>
{
    public override string Name => "link_anki_quiz";
    public override AssistantToolKind Kind => AssistantToolKind.Write;
    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Language;
    protected override string Describe(AssistantMode mode) => "Link a whole quiz to an Anki collection, keeping future quiz additions synchronized. Only use when the user wants a whole-quiz link, not first N or selected items. Adds directions without removing existing links, exclusions or study progress. Saved with Undo.";
    protected override async Task<ToolResult> RunAsync(LinkAnkiArgs args, ToolContext context, CancellationToken ct)
    {
        var target = await QuizContent.ResolveAsync(db, context, args.QuizId, ct);
        if (target.Quiz is not { } quiz) return ToolResult.Fail("Could not link quiz to Anki", target.Error!);
        var collection = await AnkiTools.OwnedAsync(db, args.CollectionId, context.UserId, ct);
        if (collection is null) return ToolResult.Fail("Could not link quiz to Anki", "Anki collection not found. Use list_anki_collections.");
        var (forward, reverse) = AnkiTools.Directions(args.Direction, collection);
        var words = args.Content is null or AnkiContent.Both or AnkiContent.Words;
        var sentences = args.Content is null or AnkiContent.Both or AnkiContent.Sentences;
        return ToolResult.Propose($"Link “{quiz.Name}” to Anki collection “{collection.Name}”, including future additions", quiz.Id,
            [QuizContent.Change(PendingChangeKinds.LinkAnkiQuiz, new AddAnkiQuizInput(collection.Id, quiz.Id,
                words && forward, words && reverse, sentences && forward, sentences && reverse))]);
    }
}

internal static class AnkiTools
{
    public static string Url(Guid id) => $"/Anki/Collection/{id}";
    public static Task<AnkiCollection?> OwnedAsync(GlosifyContext db, string id, string userId, CancellationToken ct) =>
        Guid.TryParse(id, out var parsed) ? db.AnkiCollections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == parsed && c.UserId == userId, ct) : Task.FromResult<AnkiCollection?>(null);
    public static (bool Forward, bool Reverse) Directions(AnkiDirection? direction, AnkiCollection collection)
    {
        var selected = direction is null or AnkiDirection.CollectionDefault
            ? collection.DefaultDirection == PracticeDirection.TargetToSource ? AnkiDirection.TargetToSource : AnkiDirection.SourceToTarget
            : direction;
        return (selected is AnkiDirection.SourceToTarget or AnkiDirection.Both, selected is AnkiDirection.TargetToSource or AnkiDirection.Both);
    }
}
