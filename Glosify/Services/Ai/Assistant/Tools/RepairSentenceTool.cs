using System.Text.Json;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class RepairSentenceTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "repair_sentence",
        "Propose replacing all occurrences of a quiz example sentence with a corrected natural full sentence. Queued until Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["original_text"] = StringProp("The current sentence text to replace."),
            ["new_text"] = StringProp("The corrected natural full sentence in the target language. Do not include notes, glosses, slash alternatives, or pronunciation hints."),
            ["new_translation"] = StringProp("The corrected natural translation in the source language."),
        }, required: ["original_text", "new_text", "new_translation"]));

    public AgentToolDeclaration Declaration => DeclarationValue;

    public Task<object> ExecuteAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(QueueRepairSentence(args, context));

    private static object QueueRepairSentence(JsonElement args, AgentToolContext context)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var original = GetString(args, "original_text");
        var newText = GetString(args, "new_text");
        var newTranslation = GetString(args, "new_translation");
        if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(newText) || string.IsNullOrWhiteSpace(newTranslation))
        {
            return new { error = "original_text, new_text, and new_translation are all required." };
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.RepairSentence,
            original_text = original.Trim(),
            new_text = newText.Trim(),
            new_translation = newTranslation.Trim(),
        }, JsonOptions);

        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.RepairSentence, payload));
        return new { queued = true, kind = PendingChangeKinds.RepairSentence };
    }
}
