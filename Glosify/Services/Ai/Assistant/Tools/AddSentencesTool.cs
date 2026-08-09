using System.Text.Json;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class AddSentencesTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "add_sentences",
        "Propose adding multiple standalone quiz sentences in one tool call. Prefer this over repeated add_sentence calls. Each change is queued until the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["sentences"] = SentenceArrayProp("Natural full sentences to add to the quiz."),
        }, required: ["sentences"]));

    public AgentToolDeclaration Declaration => DeclarationValue;

    public Task<object> ExecuteAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(QueueAddSentences(args, context));

    private static object QueueAddSentences(JsonElement args, AgentToolContext context)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var (sentences, skipped) = GetSentenceDrafts(args, "sentences");
        if (sentences.Count == 0)
        {
            return new { error = "At least one valid sentence and translation is required.", skipped };
        }

        foreach (var sentence in sentences)
        {
            var payload = JsonSerializer.SerializeToElement(new
            {
                kind = PendingChangeKinds.AddSentence,
                text = sentence.Text,
                translation = sentence.Translation,
            }, JsonOptions);
            context.PendingChanges.Add(new PendingChange(PendingChangeKinds.AddSentence, payload));
        }

        return new { queued = true, kind = "add_sentences", count = sentences.Count, skipped };
    }
}
