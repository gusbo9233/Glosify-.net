using System.Text.Json;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class DeleteWordTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "delete_word",
        "Propose removing a word from the quiz. Queued until the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["word_id"] = StringProp("Id of the word to delete."),
        }, required: ["word_id"]));

    public AgentToolDeclaration Declaration => DeclarationValue;

    public Task<object> ExecuteAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(QueueDeleteWord(args, context));

    private static object QueueDeleteWord(JsonElement args, AgentToolContext context)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var wordId = GetString(args, "word_id");
        if (string.IsNullOrWhiteSpace(wordId))
        {
            return new { error = "word_id is required." };
        }
        if (IsOutsideFocusedWord(wordId, context))
        {
            return FocusError(context);
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.DeleteWord,
            word_id = wordId,
        }, JsonOptions);

        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.DeleteWord, payload));
        return new { queued = true, kind = PendingChangeKinds.DeleteWord, word_id = wordId };
    }
}
