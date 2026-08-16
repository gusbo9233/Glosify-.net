using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class EditWordsTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "edit_words",
        "Propose changing multiple existing words and/or translations in one tool call. Prefer this over repeated edit_word calls when editing more than one word. Each change is queued until the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["changes"] = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["description"] = "Word edits to queue.",
                ["items"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["word_id"] = StringProp("Id of the word to edit."),
                        ["word"] = StringProp("Optional. New word or short phrase."),
                        ["translation"] = StringProp("Optional. New translation."),
                    },
                    ["required"] = new[] { "word_id" },
                },
            },
        }, required: ["changes"]));

    public AgentToolDeclaration Declaration => DeclarationValue;
    public IReadOnlyList<string> Aliases => ["edit_items"];

    private readonly GlosifyContext _context;

    public EditWordsTool(GlosifyContext context) => _context = context;

    public async Task<object> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken cancellationToken)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var (changes, skipped) = GetWordEditDrafts(args, "changes");
        if (changes.Count == 0)
        {
            return new { error = "At least one valid word edit is required.", skipped };
        }

        var outsideFocus = changes.FirstOrDefault(change => IsOutsideFocusedWord(change.WordId, context));
        if (outsideFocus.WordId is not null)
        {
            return FocusError(context);
        }

        var originals = await LoadOriginalWordsAsync(
            context.QuizId.Value,
            changes.Select(change => change.WordId).ToList(),
            cancellationToken);
        foreach (var change in changes)
        {
            originals.TryGetValue(change.WordId, out var original);
            var payload = JsonSerializer.SerializeToElement(new
            {
                kind = PendingChangeKinds.EditWord,
                word_id = change.WordId,
                original_word = original?.Word,
                original_translation = original?.Translation,
                word = change.Word,
                translation = change.Translation,
            }, JsonOptions);
            context.PendingChanges.Add(new PendingChange(PendingChangeKinds.EditWord, payload));
        }

        return new { queued = true, kind = "edit_words", count = changes.Count, skipped };
    }

    private async Task<Dictionary<string, WordDraft>> LoadOriginalWordsAsync(
        Guid quizId,
        IReadOnlyList<string> wordIds,
        CancellationToken cancellationToken)
    {
        if (wordIds.Count == 0)
        {
            return [];
        }

        return await _context.Words
            .Where(row => row.QuizId == quizId && wordIds.Contains(row.Id))
            .Select(row => new { row.Id, row.Lemma, row.Translation })
            .ToDictionaryAsync(
                row => row.Id,
                row => new WordDraft(row.Lemma, row.Translation),
                cancellationToken);
    }
}
