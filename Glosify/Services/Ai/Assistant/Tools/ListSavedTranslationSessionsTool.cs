using System.Text.Json;
using Glosify.Data;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class ListSavedTranslationSessionsTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "list_saved_translation_sessions",
        "List the user's saved Translator extension sessions with their ids, titles, dates, and translation counts. Use this when the user refers to saved translations without identifying a session. Returns up to 50 sessions per call.",
        BuildSchema(new Dictionary<string, object>
        {
            ["offset"] = IntegerProp("Optional number of sessions to skip. Defaults to 0."),
        }));

    public AgentToolDeclaration Declaration => DeclarationValue;

    private readonly GlosifyContext _context;

    public ListSavedTranslationSessionsTool(GlosifyContext context) => _context = context;

    public async Task<object> ExecuteAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        const int pageSize = 50;
        var offset = GetOffset(args);
        var query = _context.SavedTranslationSessions
            .AsNoTracking()
            .Where(session => session.UserId == context.UserId && session.Translations.Any());
        var total = await query.CountAsync(cancellationToken);
        var sessions = await query
            .OrderByDescending(session => session.UpdatedAt)
            .ThenByDescending(session => session.Id)
            .Skip(offset)
            .Take(pageSize)
            .Select(session => new
            {
                id = session.Id,
                title = session.Title,
                created_at = session.CreatedAt,
                updated_at = session.UpdatedAt,
                translation_count = session.Translations.Count,
            })
            .ToListAsync(cancellationToken);
        return new
        {
            sessions,
            total_count = total,
            offset,
            has_more = offset + sessions.Count < total,
        };
    }
}
