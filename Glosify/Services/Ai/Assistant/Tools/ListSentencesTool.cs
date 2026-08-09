using System.Text.Json;
using Glosify.Data;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class ListSentencesTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "list_sentences",
        "List the standalone quiz sentences in the current quiz with text, translation, and id. Use this before repairing or deleting sentences; repair_sentence must match the existing sentence text exactly. Returns up to 200 sentences per call; when has_more is true, call again with the next offset.",
        BuildSchema(new Dictionary<string, object>
        {
            ["offset"] = new Dictionary<string, object>
            {
                ["type"] = "integer",
                ["description"] = "Optional. Number of sentences to skip, for paging. Defaults to 0.",
            },
        }));

    public AgentToolDeclaration Declaration => DeclarationValue;

    private readonly GlosifyContext _context;

    public ListSentencesTool(GlosifyContext context) => _context = context;

    public async Task<object> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var offset = GetOffset(args);
        var query = _context.QuizSentences.Where(s => s.QuizId == context.QuizId.Value);
        var totalCount = await query.CountAsync(ct);
        var rows = await query
            .OrderBy(s => s.CreatedAt)
            .Skip(offset)
            .Take(ListPageSize)
            .Select(s => new { id = s.Id, text = s.Text, translation = s.Translation })
            .ToListAsync(ct);

        return new
        {
            sentences = rows,
            total_count = totalCount,
            offset,
            has_more = offset + rows.Count < totalCount,
        };
    }
}
