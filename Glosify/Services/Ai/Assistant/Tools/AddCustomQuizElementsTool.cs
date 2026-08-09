using System.Text.Json;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using static Glosify.Services.Ai.Assistant.Tools.CustomQuizToolSupport;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class AddCustomQuizElementsTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "add_custom_quiz_elements",
        "Propose adding one or more configured elements to an existing custom quiz. Inspect the custom quiz and list its words first. Queued until Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["custom_quiz_id"] = StringProp("Optional custom quiz id. Defaults to the custom quiz open in the creator."),
            ["blocks"] = CustomQuizBlocksProp(useWordReference: false),
        }, required: ["blocks"]));

    public AgentToolDeclaration Declaration => DeclarationValue;

    private readonly CustomQuizToolStore _customQuizzes;

    public AddCustomQuizElementsTool(CustomQuizToolStore customQuizzes) => _customQuizzes = customQuizzes;

    public async Task<object> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var resolved = ResolveCustomQuizId(args, context);
        if (resolved.Error != null)
        {
            return new { error = resolved.Error };
        }
        if (!TryGetArray(args, "blocks", out var blocks) || blocks.GetArrayLength() == 0)
        {
            return new { error = "blocks must contain at least one custom quiz element." };
        }
        var promptError = ValidateAssistantAnswerPrompts(blocks, requireAnswer: false);
        if (promptError != null)
        {
            return InvalidCustomQuizPrompts(promptError);
        }
        var item = await _customQuizzes.LoadOwnedCustomQuizAsync(resolved.Id!.Value, context.UserId, ct);
        if (item == null)
        {
            return new { error = "That custom quiz was not found." };
        }
        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.AddCustomQuizElements,
            custom_quiz_id = item.Id,
            custom_quiz_name = item.Name,
            blocks = blocks.Clone(),
        }, JsonOptions);
        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.AddCustomQuizElements, payload));
        return new { queued = true, kind = PendingChangeKinds.AddCustomQuizElements, element_count = blocks.GetArrayLength() };
    }
}
