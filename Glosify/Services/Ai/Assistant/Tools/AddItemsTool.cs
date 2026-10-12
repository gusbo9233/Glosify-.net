using System.ComponentModel;
using Glosify.Data;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed record AddItemsArgs(
    [property: Description("Quiz id. Null for the quiz this chat is attached to; use the id create_quiz returned to fill a new quiz.")] string? QuizId = null,
    [property: Description("Vocabulary: words and short phrases. Null when adding only sentences.")] IReadOnlyList<WordInput>? Words = null,
    [property: Description("Full sentences. Null when adding only words.")] IReadOnlyList<SentenceInput>? Sentences = null);

/// <summary>Adds words and sentences to a quiz in one call.</summary>
internal sealed class AddItemsTool(GlosifyContext db) : AssistantTool<AddItemsArgs>
{
    public override string Name => "add_items";

    public override AssistantToolKind Kind => AssistantToolKind.Write;

    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Language;

    protected override string Describe(AssistantMode mode) =>
        $"Add words and/or sentences to a quiz. Saved immediately; the user can undo. Words and short phrases go in words; full sentences go in sentences, never in words. At most {QuizContent.MaxItemsPerCall} of each per call: for more, call again with the rest. Items already in the quiz are not added twice.";

    protected override async Task<ToolResult> RunAsync(AddItemsArgs args, ToolContext context, CancellationToken cancellationToken)
    {
        var target = await QuizContent.ResolveAsync(db, context, args.QuizId, cancellationToken);
        if (target.Quiz is not { } quiz)
        {
            return ToolResult.Fail("Could not add to the quiz", target.Error!);
        }

        var content = QuizContent.Clean(args.Words, args.Sentences);
        if (QuizContent.WrongContentKind(context, content.Words.Count > 0, content.Sentences.Count > 0) is { } mismatch)
        {
            return ToolResult.Fail("Could not add to the quiz", mismatch);
        }

        if (content.Words.Count == 0 && content.Sentences.Count == 0)
        {
            return ToolResult.Fail("Could not add to the quiz", "Provide at least one word or sentence with a translation.", new { skipped = content.Skipped });
        }

        var changes = content.Sentences
            .Select(sentence => QuizContent.Change(PendingChangeKinds.AddSentence, new
            {
                kind = PendingChangeKinds.AddSentence,
                text = sentence.Text,
                translation = sentence.Translation,
            }))
            .Concat(content.Words.Select(word => QuizContent.Change(PendingChangeKinds.AddWord, new
            {
                kind = PendingChangeKinds.AddWord,
                word = word.Word,
                translation = word.Translation,
            })))
            .ToList();
        return ToolResult.Propose(
            $"Add {QuizContent.Describe(content.Words.Count, content.Sentences.Count, freestyle: false)} to “{quiz.Name}”",
            quiz.Id,
            changes,
            new { quiz_id = quiz.Id, skipped = content.Skipped });
    }
}

internal sealed record AddFreestyleItemsArgs(
    [property: Description("Prompt-and-answer items to add.")] IReadOnlyList<ItemInput> Items,
    [property: Description("Quiz id. Null for the quiz this chat is attached to; use the id create_quiz returned to fill a new quiz.")] string? QuizId = null);

internal sealed class AddFreestyleItemsTool(GlosifyContext db) : AssistantTool<AddFreestyleItemsArgs>
{
    public override string Name => "add_items";

    public override AssistantToolKind Kind => AssistantToolKind.Write;

    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Freestyle;

    protected override string Describe(AssistantMode mode) =>
        $"Add prompt-and-answer items to a quiz. Saved immediately; the user can undo. At most {QuizContent.MaxItemsPerCall} per call: for more, call again with the rest.";

    protected override async Task<ToolResult> RunAsync(AddFreestyleItemsArgs args, ToolContext context, CancellationToken cancellationToken)
    {
        var target = await QuizContent.ResolveAsync(db, context, args.QuizId, cancellationToken);
        if (target.Quiz is not { } quiz)
        {
            return ToolResult.Fail("Could not add to the quiz", target.Error!);
        }

        var content = QuizContent.Clean(args.Items.Select(item => new WordInput(item.Prompt, item.Answer)).ToList(), null);
        if (content.Words.Count == 0)
        {
            return ToolResult.Fail("Could not add to the quiz", "Provide at least one item with a prompt and an answer.", new { skipped = content.Skipped });
        }

        var changes = content.Words
            .Select(item => QuizContent.Change(PendingChangeKinds.AddWord, new
            {
                kind = PendingChangeKinds.AddWord,
                word = item.Word,
                translation = item.Translation,
            }))
            .ToList();
        return ToolResult.Propose(
            $"Add {QuizContent.Count(content.Words.Count, "item")} to “{quiz.Name}”",
            quiz.Id,
            changes,
            new { quiz_id = quiz.Id, skipped = content.Skipped });
    }
}
