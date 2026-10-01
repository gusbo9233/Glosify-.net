using System.ComponentModel;
using Glosify.Data;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed record SearchItemsArgs(
    [property: Description("Text to find in the target-language text or its translation.")] string Query,
    [property: Description("Which content to search. Null searches both.")] QuizItemKind? Kind = null,
    [property: Description("Quiz id. Null for the quiz this chat is attached to.")] string? QuizId = null,
    [property: Description("Maximum matches per content type, 1 to 50. Null for 20.")] int? Limit = null);

/// <summary>Finds specific words or sentences without paging through the whole quiz.</summary>
internal sealed class SearchItemsTool(GlosifyContext db) : AssistantTool<SearchItemsArgs>
{
    public override string Name => "search_items";

    public override AssistantToolKind Kind => AssistantToolKind.Read;

    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Language;

    protected override string Describe(AssistantMode mode) =>
        "Search a quiz's words and sentences, matching either the target-language text or the translation. Use this instead of paging with list_items when looking for something specific.";

    protected override async Task<ToolResult> RunAsync(SearchItemsArgs args, ToolContext context, CancellationToken cancellationToken)
    {
        var target = await QuizContent.ResolveAsync(db, context, args.QuizId, cancellationToken);
        if (target.Quiz is not { } quiz)
        {
            return ToolResult.Fail("Could not search the quiz", target.Error!);
        }

        var term = args.Query.Trim();
        if (term.Length == 0)
        {
            return ToolResult.Fail("Could not search the quiz", "query is required.");
        }

        var limit = Math.Clamp(args.Limit ?? 20, 1, 50);
        var output = new Dictionary<string, object?> { ["quiz_id"] = quiz.Id, ["query"] = term };
        var found = 0;
        if (args.Kind is null or QuizItemKind.Words)
        {
            var words = AssistantSearchQuery.WhereWordContains(
                db.Words.AsNoTracking().Where(word => word.QuizId == quiz.Id), term, db.Database);
            var rows = await words
                .OrderBy(word => word.Lemma)
                .Take(limit)
                .Select(word => new { id = word.Id, word = word.Lemma, translation = word.Translation })
                .ToListAsync(cancellationToken);
            output["words"] = rows;
            output["word_matches"] = await words.CountAsync(cancellationToken);
            found += rows.Count;
        }

        if (args.Kind is null or QuizItemKind.Sentences)
        {
            var sentences = AssistantSearchQuery.WhereSentenceContains(
                db.QuizSentences.AsNoTracking().Where(sentence => sentence.QuizId == quiz.Id), term, db.Database);
            var rows = await sentences
                .OrderBy(sentence => sentence.CreatedAt)
                .Take(limit)
                .Select(sentence => new { id = sentence.Id, text = sentence.Text, translation = sentence.Translation })
                .ToListAsync(cancellationToken);
            output["sentences"] = rows;
            output["sentence_matches"] = await sentences.CountAsync(cancellationToken);
            found += rows.Count;
        }

        return ToolResult.Ok($"Searched “{quiz.Name}” for “{term}” ({found} found)", output, quiz.Id);
    }
}

internal sealed record SearchFreestyleItemsArgs(
    [property: Description("Text to find in a prompt or answer.")] string Query,
    [property: Description("Quiz id. Null for the quiz this chat is attached to.")] string? QuizId = null,
    [property: Description("Maximum matches, 1 to 50. Null for 20.")] int? Limit = null);

internal sealed class SearchFreestyleItemsTool(GlosifyContext db) : AssistantTool<SearchFreestyleItemsArgs>
{
    public override string Name => "search_items";

    public override AssistantToolKind Kind => AssistantToolKind.Read;

    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Freestyle;

    protected override string Describe(AssistantMode mode) =>
        "Search a quiz's prompts and answers. Use this instead of paging with list_items when looking for something specific.";

    protected override async Task<ToolResult> RunAsync(SearchFreestyleItemsArgs args, ToolContext context, CancellationToken cancellationToken)
    {
        var target = await QuizContent.ResolveAsync(db, context, args.QuizId, cancellationToken);
        if (target.Quiz is not { } quiz)
        {
            return ToolResult.Fail("Could not search the quiz", target.Error!);
        }

        var term = args.Query.Trim();
        if (term.Length == 0)
        {
            return ToolResult.Fail("Could not search the quiz", "query is required.");
        }

        var items = AssistantSearchQuery.WhereWordContains(
            db.Words.AsNoTracking().Where(word => word.QuizId == quiz.Id), term, db.Database);
        var rows = await items
            .OrderBy(word => word.Lemma)
            .Take(Math.Clamp(args.Limit ?? 20, 1, 50))
            .Select(word => new { id = word.Id, prompt = word.Lemma, answer = word.Translation })
            .ToListAsync(cancellationToken);
        return ToolResult.Ok($"Searched “{quiz.Name}” for “{term}” ({rows.Count} found)", new
        {
            quiz_id = quiz.Id,
            query = term,
            items = rows,
            total_matches = await items.CountAsync(cancellationToken),
        }, quiz.Id);
    }
}
