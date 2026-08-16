using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class GetQuizSummaryTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "get_quiz_summary",
        "Get the current quiz's name, languages, collection, visibility, and word and sentence counts.",
        BuildSchema([]));

    public AgentToolDeclaration Declaration => DeclarationValue;
    public IReadOnlyList<string> Aliases => ["get_quiz_overview"];

    private readonly GlosifyContext _context;

    public GetQuizSummaryTool(GlosifyContext context) => _context = context;

    public Task<object> ExecuteAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken) =>
        GetQuizSummaryAsync(context, cancellationToken);

    private async Task<object> GetQuizSummaryAsync(AgentToolContext context, CancellationToken ct)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        var quiz = await _context.Quizzes
            .Where(q => q.Id == context.QuizId.Value && q.UserId == context.UserId)
            .Select(q => new
            {
                q.Id,
                q.Name,
                q.SourceLanguage,
                q.TargetLanguage,
                q.IsPublic,
                q.CreatedAt,
                q.CollectionId,
                CollectionName = q.Collection == null ? null : q.Collection.Name,
            })
            .FirstOrDefaultAsync(ct);
        if (quiz == null)
        {
            return QuizContextRequired();
        }

        var wordCount = await _context.Words.CountAsync(w => w.QuizId == quiz.Id, ct);
        var sentenceCount = await _context.QuizSentences.CountAsync(s => s.QuizId == quiz.Id, ct);

        if (context.IsFreestyle)
        {
            return new
            {
                id = quiz.Id,
                name = quiz.Name,
                is_public = quiz.IsPublic,
                created_at = quiz.CreatedAt,
                collection_id = quiz.CollectionId,
                collection_name = quiz.CollectionName,
                item_count = wordCount,
            };
        }

        return new
        {
            id = quiz.Id,
            name = quiz.Name,
            source_language = quiz.SourceLanguage,
            target_language = quiz.TargetLanguage,
            is_public = quiz.IsPublic,
            created_at = quiz.CreatedAt,
            collection_id = quiz.CollectionId,
            collection_name = quiz.CollectionName,
            word_count = wordCount,
            sentence_count = sentenceCount,
        };
    }
}
