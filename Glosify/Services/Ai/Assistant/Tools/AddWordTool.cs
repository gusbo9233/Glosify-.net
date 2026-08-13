using System.Text.Json;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class AddWordTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "add_word",
        "Propose adding a new word or short phrase to the quiz. Do not include example sentences here; use add_sentence for standalone quiz sentences. The change is queued; it is only saved when the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["word"] = StringProp("Word or short phrase in the target language. Prefer the exact form the learner should practice."),
            ["translation"] = StringProp("Translation in the user's source language."),
        }, required: ["word", "translation"]));

    public AgentToolDeclaration Declaration => DeclarationValue;

    public Task<object> ExecuteAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(QueueAddWord(args, context));

    private static object QueueAddWord(JsonElement args, AgentToolContext context)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        if (WrongContentKind(context, AssistantContentKind.Words) is { } mismatch)
        {
            return mismatch;
        }

        var word = GetString(args, "word");
        var translation = GetString(args, "translation");
        if (string.IsNullOrWhiteSpace(word) || string.IsNullOrWhiteSpace(translation))
        {
            return new { error = "word and translation are required." };
        }

        if (QueuedSentenceKeys(context).Contains(NormalizeForDuplicateMatch(word)))
        {
            return new
            {
                error = $"\"{word.Trim()}\" is already proposed as a sentence in this turn. "
                    + "A sentence is stored once, as a sentence.",
            };
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.AddWord,
            word = word.Trim(),
            translation = translation.Trim(),
        }, JsonOptions);

        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.AddWord, payload));
        return new { queued = true, kind = PendingChangeKinds.AddWord, word };
    }
}
