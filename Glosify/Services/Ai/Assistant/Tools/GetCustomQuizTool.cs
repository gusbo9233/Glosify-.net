using System.Text.Json;
using Glosify.Data;
using Glosify.Services.Ai.Generation;
using static Glosify.Services.Ai.Assistant.Tools.CustomQuizToolSupport;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class GetCustomQuizTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "get_custom_quiz",
        "Get a custom quiz's complete element document and validation state. In the custom quiz creator, omit custom_quiz_id to inspect the open custom quiz. Always inspect it before configuring or removing elements.",
        BuildSchema(new Dictionary<string, object>
        {
            ["custom_quiz_id"] = StringProp("Optional custom quiz id. Defaults to the custom quiz open in the creator."),
        }));

    public AgentToolDeclaration Declaration => DeclarationValue;

    private readonly GlosifyContext _context;

    public GetCustomQuizTool(GlosifyContext context) => _context = context;

    public async Task<object> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        // A quiz queued earlier in this turn has no row to read yet. Returning the open
        // quiz instead, or an error, used to read as "the new quiz isn't ready" and stop
        // the model mid-build, leaving the user an empty custom quiz to apply.
        if (string.IsNullOrWhiteSpace(GetString(args, "custom_quiz_id"))
            && !string.IsNullOrWhiteSpace(context.PendingCustomQuizRef))
        {
            return DescribeQueuedCustomQuiz(context);
        }

        var resolved = ResolveCustomQuizId(args, context);
        if (resolved.Error != null)
        {
            return new { error = resolved.Error };
        }

        var item = await new Glosify.Services.CustomQuizzes.CustomQuizService(_context)
            .GetForEditorAsync(resolved.Id!.Value, context.UserId, ct);
        if (item == null)
        {
            return new { error = "That custom quiz was not found." };
        }

        return new
        {
            id = item.Id,
            quiz_id = item.QuizId,
            name = item.Name,
            is_playable = item.IsPlayable,
            validation_errors = item.PlayabilityErrors,
            document = item.Document,
        };
    }

    /// <summary>
    /// Reports the state of a custom quiz that is still queued, assembled from the
    /// pending changes rather than the database.
    /// </summary>
}
