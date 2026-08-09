using System.Text.Json;
using Glosify.Data;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class ListQuizzesTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "list_quizzes",
        "List the user's quizzes. Use this to avoid proposing duplicate quiz names.",
        BuildSchema(new Dictionary<string, object>
        {
            ["language"] = StringProp("Optional target language to filter quizzes by. Defaults to the current app language when available."),
        }));

    public AgentToolDeclaration Declaration => DeclarationValue;

    private readonly GlosifyContext _context;

    public ListQuizzesTool(GlosifyContext context) => _context = context;

    public async Task<object> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var language = FirstNonBlank(GetString(args, "language"), context.CurrentLanguage);
        var query = _context.Quizzes.Where(q => q.UserId == context.UserId);
        if (!string.IsNullOrWhiteSpace(language))
        {
            var trimmed = language.Trim();
            query = query.Where(q => q.TargetLanguage == trimmed || q.Language == trimmed);
        }

        var rows = await query
            .OrderBy(q => q.Name)
            .Select(q => new
            {
                id = q.Id,
                name = q.Name,
                source_language = q.SourceLanguage,
                target_language = q.TargetLanguage,
                collection_id = q.CollectionId,
            })
            .ToListAsync(ct);

        return new { quizzes = rows, count = rows.Count };
    }
}
