using System.Text.Json;
using Glosify.Data;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class EditSentenceTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "edit_sentence",
        "Propose changing an existing standalone quiz sentence and/or its translation by id. The change is queued until the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["sentence_id"] = StringProp("Id of the sentence to edit. Use list_sentences to find it."),
            ["text"] = StringProp("Optional new natural full sentence in the target language."),
            ["translation"] = StringProp("Optional new translation in the source language."),
        }, required: ["sentence_id"]));

    public AgentToolDeclaration Declaration => DeclarationValue;

    private readonly GlosifyContext _context;

    public EditSentenceTool(GlosifyContext context) => _context = context;

    public async Task<object> ExecuteAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var sentenceIdText = GetString(args, "sentence_id");
        var text = GetString(args, "text")?.Trim();
        var translation = GetString(args, "translation")?.Trim();
        if (!Guid.TryParse(sentenceIdText, out var sentenceId))
        {
            return new { error = "sentence_id must be a valid id. Use list_sentences to find sentence ids." };
        }
        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(translation))
        {
            return new { error = "A new text and/or translation is required." };
        }

        var original = await _context.QuizSentences
            .Where(s => s.Id == sentenceId && s.QuizId == context.QuizId.Value)
            .Select(s => new SentenceDraft(s.Text, s.Translation))
            .FirstOrDefaultAsync(cancellationToken);
        if (original == null)
        {
            return new { error = $"Sentence {sentenceId} not found in this quiz. Use list_sentences to find sentence ids." };
        }

        QueueSentenceEdit(context, sentenceId, original, text, translation);
        return new { queued = true, kind = PendingChangeKinds.EditSentence, sentence_id = sentenceId };
    }
}
