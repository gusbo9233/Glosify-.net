using System.Text.Json;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class AddSentenceTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "add_sentence",
        "Propose adding a standalone quiz sentence. Use this only when the user asks for sentences or pasted text already contains natural full sentences. The change is queued; it is only saved when the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["text"] = StringProp("Natural full sentence in the target language. Do not include notes, glosses, slash alternatives, pronunciation hints, or fragments."),
            ["translation"] = StringProp("Natural translation in the user's source language."),
        }, required: ["text", "translation"]));

    public AgentToolDeclaration Declaration => DeclarationValue;

    public Task<object> ExecuteAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(QueueAddSentence(args, context));

    private static object QueueAddSentence(JsonElement args, AgentToolContext context)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var text = GetString(args, "text");
        var translation = GetString(args, "translation");
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(translation))
        {
            return new { error = "text and translation are required." };
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.AddSentence,
            text = text.Trim(),
            translation = translation.Trim(),
        }, JsonOptions);

        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.AddSentence, payload));
        return new { queued = true, kind = PendingChangeKinds.AddSentence };
    }
}
