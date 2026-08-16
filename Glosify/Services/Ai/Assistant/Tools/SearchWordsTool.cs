using System.Text.Json;
using Glosify.Data;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class SearchWordsTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "search_words",
        "Search the current quiz's word text and translations. Use this instead of paging through the full quiz when looking for a specific word or meaning.",
        BuildSchema(new Dictionary<string, object>
        {
            ["query"] = StringProp("Text to search for in either the target-language word or its translation."),
            ["limit"] = IntegerProp("Optional maximum number of matches to return, from 1 to 50. Defaults to 20."),
        }, required: ["query"]));

    public AgentToolDeclaration Declaration => DeclarationValue;
    public IReadOnlyList<string> Aliases => ["search_items"];

    private readonly GlosifyContext _context;

    public SearchWordsTool(GlosifyContext context) => _context = context;

    public async Task<object> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var search = GetString(args, "query")?.Trim();
        if (string.IsNullOrWhiteSpace(search))
        {
            return new { error = "query is required." };
        }

        var ownsQuiz = await _context.Quizzes
            .AnyAsync(q => q.Id == context.QuizId.Value && q.UserId == context.UserId, ct);
        if (!ownsQuiz)
        {
            return QuizContextRequired();
        }

        var normalized = search.ToLowerInvariant();
        var limit = GetBoundedInt(args, "limit", defaultValue: 20, min: 1, max: 50);
        var query = _context.Words
            .Where(w => w.QuizId == context.QuizId.Value
                && (w.Lemma.ToLower().Contains(normalized)
                    || w.Translation.ToLower().Contains(normalized)));
        var totalCount = await query.CountAsync(ct);
        var rows = await query
            .OrderBy(w => w.Lemma)
            .Take(limit)
            .Select(w => new { id = w.Id, word = w.Lemma, translation = w.Translation })
            .ToListAsync(ct);

        if (context.IsFreestyle)
        {
            return new
            {
                query = search,
                items = rows.Select(row => new { item_id = row.id, prompt = row.word, answer = row.translation }),
                total_count = totalCount,
                returned_count = rows.Count,
                has_more = rows.Count < totalCount,
            };
        }

        return new
        {
            query = search,
            words = rows,
            total_count = totalCount,
            returned_count = rows.Count,
            has_more = rows.Count < totalCount,
        };
    }
}
