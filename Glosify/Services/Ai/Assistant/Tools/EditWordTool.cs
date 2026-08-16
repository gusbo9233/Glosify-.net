using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class EditWordTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "edit_word",
        "Propose changing an existing word and/or translation. The change is queued until the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["word_id"] = StringProp("Id of the word to edit."),
            ["word"] = StringProp("Optional. New word or short phrase."),
            ["translation"] = StringProp("Optional. New translation."),
        }, required: ["word_id"]));

    public AgentToolDeclaration Declaration => DeclarationValue;
    public IReadOnlyList<string> Aliases => ["edit_item"];

    private readonly GlosifyContext _context;

    public EditWordTool(GlosifyContext context) => _context = context;

    public async Task<object> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken cancellationToken)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var wordId = FirstNonBlank(GetString(args, "word_id"), GetString(args, "item_id"));
        if (string.IsNullOrWhiteSpace(wordId))
        {
            return new { error = "word_id is required." };
        }
        if (IsOutsideFocusedWord(wordId, context))
        {
            return FocusError(context);
        }

        var original = await LoadOriginalWordAsync(context.QuizId.Value, wordId, cancellationToken);
        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.EditWord,
            word_id = wordId,
            original_word = original?.Word,
            original_translation = original?.Translation,
            word = FirstNonBlank(GetString(args, "word"), GetString(args, "prompt"))?.Trim(),
            translation = FirstNonBlank(GetString(args, "translation"), GetString(args, "answer"))?.Trim(),
        }, JsonOptions);

        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.EditWord, payload));
        return new { queued = true, kind = PendingChangeKinds.EditWord, word_id = wordId };
    }

    private async Task<WordDraft?> LoadOriginalWordAsync(Guid quizId, string wordId, CancellationToken cancellationToken)
    {
        var word = await _context.Words
            .Where(row => row.QuizId == quizId && row.Id == wordId)
            .Select(row => new WordDraft(row.Lemma, row.Translation))
            .FirstOrDefaultAsync(cancellationToken);
        return word;
    }
}
