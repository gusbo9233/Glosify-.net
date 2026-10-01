using System.ComponentModel;
using Glosify.Data;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed record DeleteItemsArgs(
    [property: Description("Quiz id. Null for the quiz this chat is attached to.")] string? QuizId = null,
    [property: Description("Ids of words to remove. Null when removing only sentences.")] IReadOnlyList<string>? WordIds = null,
    [property: Description("Ids of sentences to remove. Null when removing only words.")] IReadOnlyList<string>? SentenceIds = null);

/// <summary>Removes words and sentences. Deletion waits for the user's approval.</summary>
internal sealed class DeleteItemsTool(GlosifyContext db) : AssistantTool<DeleteItemsArgs>
{
    public override string Name => "delete_items";

    public override AssistantToolKind Kind => AssistantToolKind.Write;

    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Language;

    protected override string Describe(AssistantMode mode) =>
        "Remove words and/or sentences by id. The user is asked to approve deletions before they happen. Read the content first and never invent ids.";

    protected override Task<ToolResult> RunAsync(DeleteItemsArgs args, ToolContext context, CancellationToken cancellationToken) =>
        DeleteAsync(db, context, args.QuizId, args.WordIds ?? [], args.SentenceIds ?? [], cancellationToken);

    internal static async Task<ToolResult> DeleteAsync(
        GlosifyContext db,
        ToolContext context,
        string? quizId,
        IReadOnlyList<string> wordIds,
        IReadOnlyList<string> sentenceIds,
        CancellationToken cancellationToken)
    {
        var target = await QuizContent.ResolveAsync(db, context, quizId, cancellationToken);
        if (target.Quiz is not { } quiz)
        {
            return ToolResult.Fail("Could not remove from the quiz", target.Error!);
        }

        if (wordIds.Any(id => QuizContent.IsOutsideFocus(context, id)))
        {
            return ToolResult.Fail("Could not remove from the quiz", QuizContent.FocusError(context));
        }

        var skipped = new List<object>();
        var changes = new List<PendingChange>();
        var distinctWordIds = wordIds.Distinct().ToList();
        var words = await db.Words.AsNoTracking()
            .Where(word => word.QuizId == quiz.Id && distinctWordIds.Contains(word.Id))
            .ToDictionaryAsync(word => word.Id, cancellationToken);
        foreach (var id in distinctWordIds)
        {
            if (!words.TryGetValue(id, out var word))
            {
                skipped.Add(new { word_id = id, reason = "No word with this id in the quiz." });
                continue;
            }

            changes.Add(QuizContent.Change(PendingChangeKinds.DeleteWord, new
            {
                kind = PendingChangeKinds.DeleteWord,
                word_id = word.Id,
                word = word.Lemma,
                translation = word.Translation,
            }));
        }

        var parsedSentenceIds = sentenceIds.Distinct().ToList();
        var guids = parsedSentenceIds.Select(id => Guid.TryParse(id, out var parsed) ? parsed : Guid.Empty).ToList();
        var sentences = await db.QuizSentences.AsNoTracking()
            .Where(sentence => sentence.QuizId == quiz.Id && guids.Contains(sentence.Id))
            .ToDictionaryAsync(sentence => sentence.Id, cancellationToken);
        foreach (var id in parsedSentenceIds)
        {
            if (!Guid.TryParse(id, out var parsed) || !sentences.TryGetValue(parsed, out var sentence))
            {
                skipped.Add(new { sentence_id = id, reason = "No sentence with this id in the quiz." });
                continue;
            }

            changes.Add(QuizContent.Change(PendingChangeKinds.DeleteSentence, new
            {
                kind = PendingChangeKinds.DeleteSentence,
                sentence_id = sentence.Id,
                text = sentence.Text,
            }));
        }

        if (changes.Count == 0)
        {
            return ToolResult.Fail("Could not remove from the quiz", "None of the ids were found. Check them with list_items.", new { skipped });
        }

        var wordCount = changes.Count(change => change.Kind == PendingChangeKinds.DeleteWord);
        return ToolResult.Propose(
            $"Remove {QuizContent.Describe(wordCount, changes.Count - wordCount, context.IsFreestyle)} from “{quiz.Name}”",
            quiz.Id,
            changes,
            new { quiz_id = quiz.Id, skipped });
    }
}

internal sealed record DeleteFreestyleItemsArgs(
    [property: Description("Ids of items to remove.")] IReadOnlyList<string> ItemIds,
    [property: Description("Quiz id. Null for the quiz this chat is attached to.")] string? QuizId = null);

internal sealed class DeleteFreestyleItemsTool(GlosifyContext db) : AssistantTool<DeleteFreestyleItemsArgs>
{
    public override string Name => "delete_items";

    public override AssistantToolKind Kind => AssistantToolKind.Write;

    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Freestyle;

    protected override string Describe(AssistantMode mode) =>
        "Remove prompt-and-answer items by id. The user is asked to approve deletions before they happen. Read the items first and never invent ids.";

    protected override Task<ToolResult> RunAsync(DeleteFreestyleItemsArgs args, ToolContext context, CancellationToken cancellationToken) =>
        DeleteItemsTool.DeleteAsync(db, context, args.QuizId, args.ItemIds, [], cancellationToken);
}
