using System.Text.Json;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using static Glosify.Services.Ai.Assistant.Tools.CustomQuizToolSupport;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class RemoveCustomQuizElementTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "remove_custom_quiz_element",
        "Propose removing an element from an existing custom quiz. Inspect the quiz first and use its exact element id. Queued until Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["custom_quiz_id"] = StringProp("Optional custom quiz id. Defaults to the custom quiz open in the creator."),
            ["block_id"] = StringProp("Element id to remove."),
        }, required: ["block_id"]));

    public AgentToolDeclaration Declaration => DeclarationValue;

    private readonly CustomQuizToolStore _customQuizzes;

    public RemoveCustomQuizElementTool(CustomQuizToolStore customQuizzes) => _customQuizzes = customQuizzes;

    public async Task<object> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var resolved = ResolveCustomQuizId(args, context);
        if (resolved.Error != null)
        {
            return new { error = resolved.Error };
        }
        var blockId = GetString(args, "block_id")?.Trim();
        if (string.IsNullOrWhiteSpace(blockId))
        {
            return new { error = "block_id is required." };
        }
        var item = await _customQuizzes.LoadOwnedCustomQuizAsync(resolved.Id!.Value, context.UserId, ct);
        if (item == null)
        {
            return new { error = "That custom quiz was not found." };
        }
        if (!CustomQuizDocumentContainsBlock(item.DefinitionJson, blockId))
        {
            return new { error = $"Element {blockId} was not found in that custom quiz." };
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.RemoveCustomQuizElement,
            custom_quiz_id = item.Id,
            custom_quiz_name = item.Name,
            block_id = blockId,
        }, JsonOptions);
        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.RemoveCustomQuizElement, payload));
        return new { queued = true, kind = PendingChangeKinds.RemoveCustomQuizElement, block_id = blockId };
    }
}
