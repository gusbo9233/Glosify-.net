using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class GetWordTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "get_word",
        "Get a single word's quiz data and any matching quiz sentence by its id.",
        BuildSchema(new Dictionary<string, object>
        {
            ["word_id"] = StringProp("Id of the word to fetch."),
        }, required: ["word_id"]));

    public AgentToolDeclaration Declaration => DeclarationValue;

    private readonly GlosifyContext _context;

    public GetWordTool(GlosifyContext context) => _context = context;

    public async Task<object> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var wordId = args.TryGetProperty("word_id", out var idProp) ? idProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(wordId))
        {
            return new { error = "word_id is required." };
        }

        var word = await _context.Words.FirstOrDefaultAsync(w => w.Id == wordId && w.QuizId == context.QuizId.Value, ct);
        if (word == null)
        {
            return new { error = $"Word {wordId} not found in this quiz." };
        }

        var lemma = word.Lemma.Trim();
        var candidates = await _context.QuizSentences
            .Where(s => s.QuizId == context.QuizId.Value && s.Text.Contains(lemma))
            .ToListAsync(ct);
        var quizSentence = candidates.FirstOrDefault(s => ContainsWord(s.Text, word.Lemma));
        return new
        {
            id = word.Id,
            word = word.Lemma,
            translation = word.Translation,
            example_sentence = quizSentence?.Text ?? string.Empty,
            example_sentence_translation = quizSentence?.Translation ?? string.Empty,
        };
    }
}
