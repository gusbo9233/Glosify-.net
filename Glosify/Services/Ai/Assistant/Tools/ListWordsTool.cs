using System.Text.Json;
using Glosify.Data;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class ListWordsTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "list_words",
        "List the words in the current quiz with word text, translation, and id. Use this to see what is already in the quiz before proposing changes. Returns up to 200 words per call; when has_more is true, call again with the next offset.",
        BuildSchema(new Dictionary<string, object>
        {
            ["offset"] = new Dictionary<string, object>
            {
                ["type"] = "integer",
                ["description"] = "Optional. Number of words to skip, for paging. Defaults to 0.",
            },
        }));

    public AgentToolDeclaration Declaration => DeclarationValue;

    private readonly GlosifyContext _context;

    public ListWordsTool(GlosifyContext context) => _context = context;

    public async Task<object> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var offset = GetOffset(args);
        var query = _context.Words.Where(w => w.QuizId == context.QuizId.Value);
        var totalCount = await query.CountAsync(ct);
        var rows = await query
            .OrderBy(w => w.Lemma)
            .Skip(offset)
            .Take(ListPageSize)
            .Select(w => new { id = w.Id, word = w.Lemma, translation = w.Translation })
            .ToListAsync(ct);

        return new
        {
            words = rows,
            total_count = totalCount,
            offset,
            has_more = offset + rows.Count < totalCount,
        };
    }
}
