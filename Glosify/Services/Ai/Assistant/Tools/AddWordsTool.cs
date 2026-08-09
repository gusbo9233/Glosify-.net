using System.Text.Json;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class AddWordsTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "add_words",
        "Propose adding multiple words or short phrases to the quiz in one tool call. Prefer this over repeated add_word calls when adding more than one word. Each change is queued; it is only saved when the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["words"] = WordArrayProp("Words or short phrases to add to the quiz."),
        }, required: ["words"]));

    public AgentToolDeclaration Declaration => DeclarationValue;

    public Task<object> ExecuteAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(QueueAddWords(args, context));

    private static object QueueAddWords(JsonElement args, AgentToolContext context)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var (words, skipped) = GetWordDrafts(args, "words");
        if (words.Count == 0)
        {
            return new { error = "At least one valid word and translation is required.", skipped };
        }

        foreach (var word in words)
        {
            var payload = JsonSerializer.SerializeToElement(new
            {
                kind = PendingChangeKinds.AddWord,
                word = word.Word,
                translation = word.Translation,
            }, JsonOptions);
            context.PendingChanges.Add(new PendingChange(PendingChangeKinds.AddWord, payload));
        }

        return new { queued = true, kind = "add_words", count = words.Count, skipped };
    }
}
