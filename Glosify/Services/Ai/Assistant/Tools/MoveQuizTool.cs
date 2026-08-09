using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class MoveQuizTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "move_quiz",
        "Propose moving one of the user's quizzes into a collection. Omit collection_id to move it to the library root. The change is queued until the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["quiz_id"] = StringProp("Id of the quiz to move. Use list_quizzes to find it."),
            ["collection_id"] = StringProp("Optional destination collection id. Omit to move the quiz to the library root."),
        }, required: ["quiz_id"]));

    public AgentToolDeclaration Declaration => DeclarationValue;

    private readonly GlosifyContext _context;

    public MoveQuizTool(GlosifyContext context) => _context = context;

    public async Task<object> ExecuteAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        var quizIdText = GetString(args, "quiz_id");
        var destination = GetNullableGuidString(args, "collection_id");
        if (!Guid.TryParse(quizIdText, out var quizId))
        {
            return new { error = "quiz_id must be a valid id. Use list_quizzes to find quiz ids." };
        }
        if (destination.Invalid)
        {
            return new { error = "collection_id must be a valid id." };
        }

        var quiz = await _context.Quizzes
            .FirstOrDefaultAsync(q => q.Id == quizId && q.UserId == context.UserId, cancellationToken);
        if (quiz == null)
        {
            return new { error = $"Quiz {quizId} was not found." };
        }

        Collection? collection = null;
        if (destination.Value.HasValue)
        {
            collection = await _context.Collections.FirstOrDefaultAsync(
                c => c.Id == destination.Value.Value
                    && c.UserId == context.UserId
                    && c.Language == quiz.TargetLanguage,
                cancellationToken);
            if (collection == null)
            {
                return new { error = "The destination collection was not found for this quiz's language." };
            }
        }

        if (quiz.CollectionId == destination.Value)
        {
            return new { error = "The quiz is already in that location." };
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.MoveQuiz,
            quiz_id = quiz.Id,
            quiz_name = quiz.Name,
            collection_id = destination.Value,
            collection_name = collection?.Name,
        }, JsonOptions);
        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.MoveQuiz, payload));
        return new { queued = true, kind = PendingChangeKinds.MoveQuiz, quiz_id = quiz.Id };
    }
}
