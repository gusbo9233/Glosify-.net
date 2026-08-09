using System.Text.Json;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using static Glosify.Services.Ai.Assistant.Tools.CustomQuizToolSupport;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class ConfigureCustomQuizElementTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "configure_custom_quiz_element",
        "Propose changing an existing custom quiz element. Only supplied settings are changed. options replaces the full option list. Queued until Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["custom_quiz_id"] = StringProp("Optional custom quiz id. Defaults to the custom quiz open in the creator."),
            ["block_id"] = StringProp("Existing element id from get_custom_quiz."),
            ["settings"] = CustomQuizBlockProp(useWordReference: false, requireType: false),
        }, required: ["block_id", "settings"]));

    public AgentToolDeclaration Declaration => DeclarationValue;

    private readonly CustomQuizToolStore _customQuizzes;

    public ConfigureCustomQuizElementTool(CustomQuizToolStore customQuizzes) => _customQuizzes = customQuizzes;

    public async Task<object> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var resolved = ResolveCustomQuizId(args, context);
        if (resolved.Error != null)
        {
            return new { error = resolved.Error };
        }
        var blockId = GetString(args, "block_id")?.Trim();
        if (string.IsNullOrWhiteSpace(blockId)
            || !args.TryGetProperty("settings", out var settings)
            || settings.ValueKind != JsonValueKind.Object)
        {
            return new { error = "block_id and settings are required." };
        }
        var item = await _customQuizzes.LoadOwnedCustomQuizAsync(resolved.Id!.Value, context.UserId, ct);
        if (item == null)
        {
            return new { error = "That custom quiz was not found." };
        }
        if (settings.TryGetProperty("label", out var configuredLabel)
            && (configuredLabel.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(configuredLabel.GetString())))
        {
            return InvalidCustomQuizPrompts($"Element {blockId} cannot have an empty question label.");
        }
        if (!CustomQuizDocumentContainsBlock(item.DefinitionJson, blockId))
        {
            return new { error = $"Element {blockId} was not found in that custom quiz. Inspect it again before configuring elements." };
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.ConfigureCustomQuizElement,
            custom_quiz_id = item.Id,
            custom_quiz_name = item.Name,
            block_id = blockId,
            settings = settings.Clone(),
        }, JsonOptions);
        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.ConfigureCustomQuizElement, payload));
        return new { queued = true, kind = PendingChangeKinds.ConfigureCustomQuizElement, block_id = blockId };
    }
}
