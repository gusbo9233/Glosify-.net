using System.Text.Json;
using Glosify.Data;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class EditSentencesTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "edit_sentences",
        "Propose changing multiple existing standalone quiz sentences and/or translations by id. Prefer this over repeated edit_sentence calls. Each change is queued until the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["changes"] = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["description"] = "Sentence edits to queue.",
                ["items"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["sentence_id"] = StringProp("Id of the sentence to edit."),
                        ["text"] = StringProp("Optional new natural full sentence in the target language."),
                        ["translation"] = StringProp("Optional new translation in the source language."),
                    },
                    ["required"] = new[] { "sentence_id" },
                },
            },
        }, required: ["changes"]));

    public AgentToolDeclaration Declaration => DeclarationValue;

    private readonly GlosifyContext _context;

    public EditSentencesTool(GlosifyContext context) => _context = context;

    public async Task<object> ExecuteAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var (changes, parsedSkipped) = GetSentenceEditDrafts(args, "changes");
        var skipped = parsedSkipped.ToList();
        if (changes.Count == 0)
        {
            return new { error = "At least one valid sentence edit is required.", skipped };
        }

        var sentenceIds = changes.Select(change => change.SentenceId).Distinct().ToList();
        var originals = await _context.QuizSentences
            .Where(s => s.QuizId == context.QuizId.Value && sentenceIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Text, s.Translation })
            .ToDictionaryAsync(
                s => s.Id,
                s => new SentenceDraft(s.Text, s.Translation),
                cancellationToken);

        var queued = 0;
        foreach (var change in changes)
        {
            if (!originals.TryGetValue(change.SentenceId, out var original))
            {
                skipped.Add(new SkippedItem(change.Index, "Sentence was not found in this quiz."));
                continue;
            }

            QueueSentenceEdit(context, change.SentenceId, original, change.Text, change.Translation);
            queued++;
        }

        if (queued == 0)
        {
            return new { error = "None of the requested sentences were found in this quiz.", skipped };
        }

        return new { queued = true, kind = "edit_sentences", count = queued, skipped };
    }
}
