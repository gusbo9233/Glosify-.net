using Glosify.Data;
using Glosify.Services.Anki;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed record RenameAnkiArgs(string CollectionId, string Name);
internal sealed record RemoveAnkiCardsArgs(string CollectionId, IReadOnlyList<Guid> CardIds);
internal sealed record UnlinkAnkiQuizArgs(string CollectionId, string? QuizId = null);

internal sealed class RenameAnkiCollectionTool(GlosifyContext db) : AssistantTool<RenameAnkiArgs>
{
    public override string Name => "rename_anki_collection";
    public override AssistantToolKind Kind => AssistantToolKind.Write;
    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Language;
    protected override string Describe(AssistantMode mode) => "Rename an owned Anki collection. Saved with Undo. Does not change cards, schedules or review history.";
    protected override async Task<ToolResult> RunAsync(RenameAnkiArgs args, ToolContext context, CancellationToken ct)
    {
        var collection = await AnkiTools.OwnedAsync(db, args.CollectionId, context.UserId, ct);
        if (collection is null) return ToolResult.Fail("Could not rename collection", "Anki collection not found.");
        var name = args.Name.Trim();
        if (name.Length is < 1 or > 160) return ToolResult.Fail("Could not rename collection", "Use a name between 1 and 160 characters.");
        var summary = $"Rename Anki collection “{collection.Name}” to “{name}”";
        return ToolResult.Propose(summary, null, [QuizContent.Change(PendingChangeKinds.RenameAnkiCollection,
            new { input = new AnkiRenameInput(collection.Id, name, AnkiCollectionState.From(collection)), summary })]);
    }
}

internal sealed class RemoveAnkiCardsTool(GlosifyContext db, IAnkiCollectionService anki) : AssistantTool<RemoveAnkiCardsArgs>
{
    public override string Name => "remove_anki_cards";
    public override AssistantToolKind Kind => AssistantToolKind.Write;
    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Language;
    protected override string Describe(AssistantMode mode) => "Remove 1-100 selected card ids from study, with approval and Undo. Read get_anki_collection first. Preserves review history and scheduling; quiz-linked cards are excluded from synchronization. Does not delete the source words or sentences.";
    protected override async Task<ToolResult> RunAsync(RemoveAnkiCardsArgs args, ToolContext context, CancellationToken ct)
    {
        var collection = await AnkiTools.OwnedAsync(db, args.CollectionId, context.UserId, ct);
        if (collection is null) return ToolResult.Fail("Could not remove cards", "Anki collection not found.");
        try
        {
            var ids = args.CardIds.Distinct().ToArray();
            var cards = await anki.ReadCardStatesAsync(collection.Id, ids, context.UserId, ct);
            var labels = await db.AnkiCards.AsNoTracking().Where(c => ids.Contains(c.Id) && c.Note.AnkiCollectionId == collection.Id)
                .OrderBy(c => c.Note.TargetText).Take(5).Select(c => c.Note.TargetText + " (" + c.Direction + ")").ToListAsync(ct);
            var summary = $"Remove {cards.Count} cards from “{collection.Name}”: {string.Join(", ", labels)}{(cards.Count > 5 ? ", …" : "")}. Review history retained.";
            return ToolResult.Propose(summary, null, [QuizContent.Change(PendingChangeKinds.RemoveAnkiCards,
                new { input = new RemoveAnkiCardsInput(collection.Id, cards), summary })]);
        }
        catch (AnkiValidationException ex) { return ToolResult.Fail("Could not remove cards", ex.Message); }
    }
}

internal sealed class UnlinkAnkiQuizTool(GlosifyContext db, IAnkiCollectionService anki) : AssistantTool<UnlinkAnkiQuizArgs>
{
    public override string Name => "unlink_anki_quiz";
    public override AssistantToolKind Kind => AssistantToolKind.Write;
    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Language;
    protected override string Describe(AssistantMode mode) => "Stop synchronizing a quiz to an Anki collection, with approval and Undo. Cards included only through this link leave study; directly added cards remain. Preserves source content, scheduling and review history.";
    protected override async Task<ToolResult> RunAsync(UnlinkAnkiQuizArgs args, ToolContext context, CancellationToken ct)
    {
        var collection = await AnkiTools.OwnedAsync(db, args.CollectionId, context.UserId, ct);
        var target = await QuizContent.ResolveAsync(db, context, args.QuizId, ct);
        if (collection is null || target.Quiz is null) return ToolResult.Fail("Could not unlink quiz", target.Error ?? "Anki collection not found.");
        var snapshot = await anki.ReadLinkStateAsync(collection.Id, target.Quiz.Id, context.UserId, ct);
        if (snapshot.Link is null) return ToolResult.Fail("Could not unlink quiz", "This quiz is not linked to that collection.");
        var count = snapshot.Cards.Count(c => c.IsActive && c.QuizLinkIncluded && !c.DirectlyIncluded);
        var summary = $"Unlink “{target.Quiz.Name}” from “{collection.Name}”; {count} cards leave study. Directly added cards and review history remain.";
        return ToolResult.Propose(summary, null, [QuizContent.Change(PendingChangeKinds.UnlinkAnkiQuiz, new { input = snapshot, summary })]);
    }
}
