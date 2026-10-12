using System.ComponentModel;
using Glosify.Data;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed record ListItemsArgs(
    [property: Description("Which content to list.")] QuizItemKind Kind,
    [property: Description("Quiz id. Null for the quiz this chat is attached to.")] string? QuizId = null,
    [property: Description("Number of rows to skip for paging. Null for 0.")] int? Offset = null);

/// <summary>Pages through a quiz's words or sentences with their ids.</summary>
internal sealed class ListItemsTool(GlosifyContext db) : AssistantTool<ListItemsArgs>
{
    public override string Name => "list_items";

    public override AssistantToolKind Kind => AssistantToolKind.Read;

    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Language;

    protected override string Describe(AssistantMode mode) =>
        $"List a quiz's words or sentences with their ids. Returns up to {QuizContent.PageSize} rows; when has_more is true, call again with next_offset. Read before editing or deleting.";

    protected override async Task<ToolResult> RunAsync(ListItemsArgs args, ToolContext context, CancellationToken cancellationToken)
    {
        var target = await QuizContent.ResolveAsync(db, context, args.QuizId, cancellationToken);
        if (target.Quiz is not { } quiz)
        {
            return ToolResult.Fail("Could not list quiz content", target.Error!);
        }

        var offset = Math.Max(0, args.Offset ?? 0);
        if (args.Kind == QuizItemKind.Sentences)
        {
            var sentences = db.QuizSentences.AsNoTracking().Where(sentence => sentence.QuizId == quiz.Id);
            var total = await sentences.CountAsync(cancellationToken);
            var rows = await sentences
                .OrderBy(sentence => sentence.CreatedAt)
                .ThenBy(sentence => sentence.Id)
                .Skip(offset)
                .Take(QuizContent.PageSize)
                .Select(sentence => new { id = sentence.Id, text = sentence.Text, translation = sentence.Translation })
                .ToListAsync(cancellationToken);
            return ToolResult.Ok(
                $"Listed {QuizContent.Count(rows.Count, "sentence")} in “{quiz.Name}”",
                Page(quiz.Id, "sentences", rows, offset, total), quiz.Id);
        }

        var words = db.Words.AsNoTracking().Where(word => word.QuizId == quiz.Id);
        var wordTotal = await words.CountAsync(cancellationToken);
        var wordRows = await words
            .OrderBy(word => word.Lemma)
            .ThenBy(word => word.Id)
            .Skip(offset)
            .Take(QuizContent.PageSize)
            .Select(word => new { id = word.Id, word = word.Lemma, translation = word.Translation })
            .ToListAsync(cancellationToken);
        return ToolResult.Ok(
            $"Listed {QuizContent.Count(wordRows.Count, "word")} in “{quiz.Name}”",
            Page(quiz.Id, "words", wordRows, offset, wordTotal), quiz.Id);
    }

    internal static object Page<T>(Guid quizId, string name, IReadOnlyList<T> rows, int offset, int total) =>
        new Dictionary<string, object?>
        {
            ["quiz_id"] = quizId,
            [name] = rows,
            ["total_count"] = total,
            ["offset"] = offset,
            ["has_more"] = offset + rows.Count < total,
            ["next_offset"] = offset + rows.Count < total ? offset + rows.Count : null,
        };
}

internal sealed record ListFreestyleItemsArgs(
    [property: Description("Quiz id. Null for the quiz this chat is attached to.")] string? QuizId = null,
    [property: Description("Number of items to skip for paging. Null for 0.")] int? Offset = null);

internal sealed class ListFreestyleItemsTool(GlosifyContext db) : AssistantTool<ListFreestyleItemsArgs>
{
    public override string Name => "list_items";

    public override AssistantToolKind Kind => AssistantToolKind.Read;

    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Freestyle;

    protected override string Describe(AssistantMode mode) =>
        $"List a quiz's prompt-and-answer items with their ids. Returns up to {QuizContent.PageSize} items; when has_more is true, call again with next_offset. Read before editing or deleting.";

    protected override async Task<ToolResult> RunAsync(ListFreestyleItemsArgs args, ToolContext context, CancellationToken cancellationToken)
    {
        var target = await QuizContent.ResolveAsync(db, context, args.QuizId, cancellationToken);
        if (target.Quiz is not { } quiz)
        {
            return ToolResult.Fail("Could not list quiz items", target.Error!);
        }

        var offset = Math.Max(0, args.Offset ?? 0);
        var items = db.Words.AsNoTracking().Where(word => word.QuizId == quiz.Id);
        var total = await items.CountAsync(cancellationToken);
        var rows = await items
            .OrderBy(word => word.Lemma)
            .ThenBy(word => word.Id)
            .Skip(offset)
            .Take(QuizContent.PageSize)
            .Select(word => new { id = word.Id, prompt = word.Lemma, answer = word.Translation })
            .ToListAsync(cancellationToken);
        return ToolResult.Ok(
            $"Listed {QuizContent.Count(rows.Count, "item")} in “{quiz.Name}”",
            ListItemsTool.Page(quiz.Id, "items", rows, offset, total), quiz.Id);
    }
}
