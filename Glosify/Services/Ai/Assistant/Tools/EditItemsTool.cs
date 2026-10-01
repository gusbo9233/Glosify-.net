using System.ComponentModel;
using Glosify.Data;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed record WordEdit(
    [property: Description("Id of the word, from list_items or search_items.")] string Id,
    [property: Description("New word or short phrase. Null to keep it.")] string? Word = null,
    [property: Description("New translation. Null to keep it.")] string? Translation = null);

internal sealed record SentenceEdit(
    [property: Description("Id of the sentence, from list_items or search_items.")] string Id,
    [property: Description("New full sentence. Null to keep it.")] string? Text = null,
    [property: Description("New translation. Null to keep it.")] string? Translation = null);

internal sealed record EditItemsArgs(
    [property: Description("Quiz id. Null for the quiz this chat is attached to.")] string? QuizId = null,
    [property: Description("Word edits. Null when editing only sentences.")] IReadOnlyList<WordEdit>? Words = null,
    [property: Description("Sentence edits. Null when editing only words.")] IReadOnlyList<SentenceEdit>? Sentences = null);

/// <summary>Changes existing words and sentences by id.</summary>
internal sealed class EditItemsTool(GlosifyContext db) : AssistantTool<EditItemsArgs>
{
    public override string Name => "edit_items";

    public override AssistantToolKind Kind => AssistantToolKind.Write;

    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Language;

    protected override string Describe(AssistantMode mode) =>
        "Change existing words and/or sentences by id. Saved immediately; the user can undo. Read the current content first with list_items or search_items and never invent ids.";

    protected override Task<ToolResult> RunAsync(EditItemsArgs args, ToolContext context, CancellationToken cancellationToken) =>
        EditAsync(db, context, args.QuizId, args.Words ?? [], args.Sentences ?? [], cancellationToken);

    internal static async Task<ToolResult> EditAsync(
        GlosifyContext db,
        ToolContext context,
        string? quizId,
        IReadOnlyList<WordEdit> wordEdits,
        IReadOnlyList<SentenceEdit> sentenceEdits,
        CancellationToken cancellationToken)
    {
        var target = await QuizContent.ResolveAsync(db, context, quizId, cancellationToken);
        if (target.Quiz is not { } quiz)
        {
            return ToolResult.Fail("Could not edit the quiz", target.Error!);
        }

        if (wordEdits.FirstOrDefault(edit => QuizContent.IsOutsideFocus(context, edit.Id)) is not null)
        {
            return ToolResult.Fail("Could not edit the quiz", QuizContent.FocusError(context));
        }

        var skipped = new List<object>();
        var changes = new List<PendingChange>();
        var wordIds = wordEdits.Select(edit => edit.Id).Distinct().ToList();
        var words = await db.Words.AsNoTracking()
            .Where(word => word.QuizId == quiz.Id && wordIds.Contains(word.Id))
            .ToDictionaryAsync(word => word.Id, cancellationToken);
        foreach (var edit in wordEdits)
        {
            if (!words.TryGetValue(edit.Id, out var original))
            {
                skipped.Add(new { word_id = edit.Id, reason = "No word with this id in the quiz." });
                continue;
            }

            var word = Blank(edit.Word) ? null : edit.Word!.Trim();
            var translation = Blank(edit.Translation) ? null : edit.Translation!.Trim();
            if ((word ?? original.Lemma) == original.Lemma && (translation ?? original.Translation) == original.Translation)
            {
                skipped.Add(new { word_id = edit.Id, reason = "Nothing to change." });
                continue;
            }

            changes.Add(QuizContent.Change(PendingChangeKinds.EditWord, new
            {
                kind = PendingChangeKinds.EditWord,
                word_id = original.Id,
                original_word = original.Lemma,
                original_translation = original.Translation,
                word,
                translation,
            }));
        }

        var sentenceIds = sentenceEdits.Select(edit => Guid.TryParse(edit.Id, out var id) ? id : Guid.Empty).Distinct().ToList();
        var sentences = await db.QuizSentences.AsNoTracking()
            .Where(sentence => sentence.QuizId == quiz.Id && sentenceIds.Contains(sentence.Id))
            .ToDictionaryAsync(sentence => sentence.Id, cancellationToken);
        foreach (var edit in sentenceEdits)
        {
            if (!Guid.TryParse(edit.Id, out var id) || !sentences.TryGetValue(id, out var original))
            {
                skipped.Add(new { sentence_id = edit.Id, reason = "No sentence with this id in the quiz." });
                continue;
            }

            var text = Blank(edit.Text) ? null : edit.Text!.Trim();
            var translation = Blank(edit.Translation) ? null : edit.Translation!.Trim();
            if ((text ?? original.Text) == original.Text && (translation ?? original.Translation) == original.Translation)
            {
                skipped.Add(new { sentence_id = edit.Id, reason = "Nothing to change." });
                continue;
            }

            changes.Add(QuizContent.Change(PendingChangeKinds.EditSentence, new
            {
                kind = PendingChangeKinds.EditSentence,
                sentence_id = original.Id,
                original_text = original.Text,
                original_translation = original.Translation,
                text,
                translation,
            }));
        }

        if (changes.Count == 0)
        {
            return ToolResult.Fail("Could not edit the quiz", "No valid edits. Check the ids with list_items or search_items.", new { skipped });
        }

        var wordCount = changes.Count(change => change.Kind == PendingChangeKinds.EditWord);
        return ToolResult.Propose(
            $"Edit {QuizContent.Describe(wordCount, changes.Count - wordCount, context.IsFreestyle)} in “{quiz.Name}”",
            quiz.Id,
            changes,
            new { quiz_id = quiz.Id, skipped });
    }

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
}

internal sealed record ItemEdit(
    [property: Description("Id of the item, from list_items or search_items.")] string Id,
    [property: Description("New prompt. Null to keep it.")] string? Prompt = null,
    [property: Description("New answer. Null to keep it.")] string? Answer = null);

internal sealed record EditFreestyleItemsArgs(
    [property: Description("Item edits.")] IReadOnlyList<ItemEdit> Items,
    [property: Description("Quiz id. Null for the quiz this chat is attached to.")] string? QuizId = null);

internal sealed class EditFreestyleItemsTool(GlosifyContext db) : AssistantTool<EditFreestyleItemsArgs>
{
    public override string Name => "edit_items";

    public override AssistantToolKind Kind => AssistantToolKind.Write;

    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Freestyle;

    protected override string Describe(AssistantMode mode) =>
        "Change existing prompt-and-answer items by id. Saved immediately; the user can undo. Read the current items first and never invent ids.";

    protected override Task<ToolResult> RunAsync(EditFreestyleItemsArgs args, ToolContext context, CancellationToken cancellationToken) =>
        EditItemsTool.EditAsync(
            db,
            context,
            args.QuizId,
            args.Items.Select(item => new WordEdit(item.Id, item.Prompt, item.Answer)).ToList(),
            [],
            cancellationToken);
}
