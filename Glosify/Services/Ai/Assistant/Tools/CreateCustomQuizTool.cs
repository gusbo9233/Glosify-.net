using System.Text.Json;
using Glosify.Data;
using Glosify.Models.CustomQuizzes;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;
using static Glosify.Services.Ai.Assistant.Tools.CustomQuizToolSupport;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class CreateCustomQuizTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "create_custom_quiz",
        "Start a new empty custom quiz for an existing backing quiz. Do NOT call this when a custom quiz is already open in the creator; add elements to that one instead. This queues only the quiz shell, but the shell is a valid target for the rest of this turn: immediately add every element with a separate add_label, add_text_input, add_checkbox, add_choice, add_word_bank, add_submit_button, add_feedback_message, or add_custom_quiz_element call in this same turn. Never pass a complete document here, and never stop after the shell to wait for the user to apply it.",
        BuildSchema(new Dictionary<string, object>
        {
            ["quiz_id"] = StringProp("Optional backing quiz id. Defaults to the selected quiz."),
            ["name"] = StringProp("Custom quiz name."),
            ["template_id"] = StringProp("Optional id from list_custom_quiz_templates. Sets the visual style; follow that template's layout guidance when adding elements."),
            ["create_additional_quiz"] = BoolProp("Set true only when a custom quiz is already open in the creator and the user explicitly asked for a second, separate custom quiz."),
        }, required: ["name"]));

    public AgentToolDeclaration Declaration => DeclarationValue;

    private readonly GlosifyContext _context;

    public CreateCustomQuizTool(GlosifyContext context) => _context = context;

    public async Task<object> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var quizId = context.QuizId;
        var suppliedQuizId = GetString(args, "quiz_id");
        if (!string.IsNullOrWhiteSpace(suppliedQuizId))
        {
            if (!Guid.TryParse(suppliedQuizId, out var parsed))
            {
                return new { error = "quiz_id must be a valid id." };
            }
            quizId = parsed;
        }

        var name = GetString(args, "name")?.Trim();
        var template = ResolveCustomQuizTemplate(args);
        if (!quizId.HasValue || string.IsNullOrWhiteSpace(name))
        {
            return new { error = "Choose a backing quiz and provide a custom quiz name." };
        }
        // Creating a second quiz while the creator has one open used to strand the open
        // editor: every later element call in the turn resolves to the new draft first.
        if (context.CustomQuizId.HasValue && !GetBool(args, "create_additional_quiz"))
        {
            return new
            {
                error = "A custom quiz is already open in the creator, so there is nothing to create.",
                open_custom_quiz_id = context.CustomQuizId.Value,
                correction = "Call get_custom_quiz to inspect the open custom quiz, then add, configure, or remove its elements. Only call create_custom_quiz again with create_additional_quiz set to true if the user explicitly asked for a second, separate custom quiz.",
            };
        }
        if (args.TryGetProperty("blocks", out _))
        {
            return new { error = "create_custom_quiz queues only the empty quiz shell. Call one element tool per element after creating the shell." };
        }
        if (!await _context.Quizzes.AnyAsync(quiz => quiz.Id == quizId.Value && quiz.UserId == context.UserId, ct))
        {
            return QuizContextRequired();
        }

        var draftRef = $"custom-{Guid.NewGuid():N}";
        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.CreateCustomQuiz,
            quiz_id = quizId.Value,
            name,
            draft_ref = draftRef,
            style_preset = template?.StylePreset ?? CustomQuizStylePresets.Editorial,
        }, JsonOptions);
        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.CreateCustomQuiz, payload));
        context.PendingCustomQuizRef = draftRef;
        context.PendingCustomQuizName = name;
        return new
        {
            queued = true,
            kind = PendingChangeKinds.CreateCustomQuiz,
            name,
            draft_ref = draftRef,
            next = "Add every element now, in this same turn, with one element tool call each. Element calls default to this new quiz, so leave custom_quiz_id out.",
            important = "This shell is only half the work. Do not end your turn and do not ask the user to apply anything yet: applying now would produce an empty custom quiz.",
        };
    }
}
