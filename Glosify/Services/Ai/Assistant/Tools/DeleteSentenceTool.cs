using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class DeleteSentenceTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "delete_sentence",
        "Propose removing a standalone quiz sentence by its id. Use list_sentences to find the id. Queued until the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["sentence_id"] = StringProp("Id of the sentence to delete."),
        }, required: ["sentence_id"]));

    public AgentToolDeclaration Declaration => DeclarationValue;

    private readonly GlosifyContext _context;

    public DeleteSentenceTool(GlosifyContext context) => _context = context;

    public async Task<object> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var sentenceIdText = GetString(args, "sentence_id");
        if (!Guid.TryParse(sentenceIdText, out var sentenceId))
        {
            return new { error = "sentence_id must be a valid id. Use list_sentences to find sentence ids." };
        }

        var sentence = await _context.QuizSentences
            .FirstOrDefaultAsync(s => s.Id == sentenceId && s.QuizId == context.QuizId.Value, ct);
        if (sentence == null)
        {
            return new { error = $"Sentence {sentenceId} not found in this quiz. Use list_sentences to find sentence ids." };
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.DeleteSentence,
            sentence_id = sentence.Id,
            text = sentence.Text,
        }, JsonOptions);

        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.DeleteSentence, payload));
        return new { queued = true, kind = PendingChangeKinds.DeleteSentence, sentence_id = sentence.Id };
    }
}
