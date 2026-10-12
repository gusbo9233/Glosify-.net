using System.ComponentModel;
using Glosify.Data;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed record QuizOverviewArgs(
    [property: Description("Quiz id. Null for the quiz this chat is attached to.")] string? QuizId = null);

internal sealed class QuizOverviewTool(GlosifyContext db) : AssistantTool<QuizOverviewArgs>
{
    public override string Name => "quiz_overview";

    public override AssistantToolKind Kind => AssistantToolKind.Read;

    protected override string Describe(AssistantMode mode) => mode == AssistantMode.Freestyle
        ? "Get a quiz's name, collection, visibility, and item count."
        : "Get a quiz's name, languages, collection, visibility, and word and sentence counts.";

    protected override async Task<ToolResult> RunAsync(QuizOverviewArgs args, ToolContext context, CancellationToken cancellationToken)
    {
        var target = await QuizContent.ResolveAsync(db, context, args.QuizId, cancellationToken);
        if (target.Quiz is not { } quiz)
        {
            return ToolResult.Fail("Could not read the quiz", target.Error!);
        }

        var collection = quiz.CollectionId is Guid collectionId
            ? await db.Collections.AsNoTracking().Where(c => c.Id == collectionId).Select(c => c.Name).FirstOrDefaultAsync(cancellationToken)
            : null;
        var words = await db.Words.CountAsync(word => word.QuizId == quiz.Id, cancellationToken);
        var title = $"Read overview of “{quiz.Name}”";
        if (context.IsFreestyle)
        {
            return ToolResult.Ok(title, new
            {
                id = quiz.Id,
                name = quiz.Name,
                is_public = quiz.IsPublic,
                collection_id = quiz.CollectionId,
                collection_name = collection,
                item_count = words,
            }, quiz.Id);
        }

        var sentences = await db.QuizSentences.CountAsync(sentence => sentence.QuizId == quiz.Id, cancellationToken);
        return ToolResult.Ok(title, new
        {
            id = quiz.Id,
            name = quiz.Name,
            source_language = quiz.SourceLanguage,
            target_language = quiz.TargetLanguage,
            is_public = quiz.IsPublic,
            collection_id = quiz.CollectionId,
            collection_name = collection,
            word_count = words,
            sentence_count = sentences,
        }, quiz.Id);
    }
}
