using System.Text.Json;
using Glosify.Data;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class ListCollectionsTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "list_collections",
        "List the user's collections for the current language. Use this before proposing nested collections or placing a quiz into an existing collection.",
        BuildSchema(new Dictionary<string, object>
        {
            ["language"] = StringProp("Optional language to filter collections by. Defaults to the current app language when available."),
        }));

    public AgentToolDeclaration Declaration => DeclarationValue;

    private readonly GlosifyContext _context;

    public ListCollectionsTool(GlosifyContext context) => _context = context;

    public async Task<object> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var language = FirstNonBlank(GetString(args, "language"), context.CurrentLanguage);
        if (string.IsNullOrWhiteSpace(language))
        {
            return new { error = "language is required when no current app language is selected." };
        }

        var rows = await _context.Collections
            .Where(c => c.UserId == context.UserId && c.Language == language.Trim())
            .OrderBy(c => c.ParentCollectionId.HasValue)
            .ThenBy(c => c.Name)
            .Select(c => new
            {
                id = c.Id,
                name = c.Name,
                language = c.Language,
                parent_collection_id = c.ParentCollectionId,
            })
            .ToListAsync(ct);

        return new { collections = rows, count = rows.Count };
    }
}
