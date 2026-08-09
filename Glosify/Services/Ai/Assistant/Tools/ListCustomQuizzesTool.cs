using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class ListCustomQuizzesTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "list_custom_quizzes",
        "List custom quizzes owned by the user, optionally limited to a backing quiz. Use this to find an existing custom quiz before inspecting or changing its elements.",
        BuildSchema(new Dictionary<string, object>
        {
            ["quiz_id"] = StringProp("Optional backing quiz id. Defaults to the current quiz when one is selected."),
        }));

    public AgentToolDeclaration Declaration => DeclarationValue;

    private readonly GlosifyContext _context;

    public ListCustomQuizzesTool(GlosifyContext context) => _context = context;

    public async Task<object> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        Guid? quizId = context.QuizId;
        var suppliedQuizId = GetString(args, "quiz_id");
        if (!string.IsNullOrWhiteSpace(suppliedQuizId))
        {
            if (!Guid.TryParse(suppliedQuizId, out var parsed))
            {
                return new { error = "quiz_id must be a valid id." };
            }
            quizId = parsed;
        }

        var query = _context.CustomQuizzes
            .AsNoTracking()
            .Where(item => item.Quiz.UserId == context.UserId);
        if (quizId.HasValue)
        {
            query = query.Where(item => item.QuizId == quizId.Value);
        }

        var rows = await query
            .OrderBy(item => item.Quiz.Name)
            .ThenBy(item => item.Name)
            .Select(item => new
            {
                id = item.Id,
                name = item.Name,
                quiz_id = item.QuizId,
                quiz_name = item.Quiz.Name,
                is_playable = item.IsPlayable,
                updated_at = item.UpdatedAt,
            })
            .ToListAsync(ct);
        return new { custom_quizzes = rows, count = rows.Count };
    }
}
