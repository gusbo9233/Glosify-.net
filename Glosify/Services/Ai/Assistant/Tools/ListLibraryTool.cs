using System.ComponentModel;
using Glosify.Data;
using Glosify.Services.Language;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed record ListLibraryArgs(
    [property: Description("Learning language to list. Null for the current app language.")] string? Language = null);

/// <summary>The user's collections and quizzes for one language, with ids.</summary>
internal sealed class ListLibraryTool(GlosifyContext db) : AssistantTool<ListLibraryArgs>
{
    private const int MaximumQuizzes = 300;

    public override string Name => "list_library";

    public override AssistantToolKind Kind => AssistantToolKind.Read;

    protected override string Describe(AssistantMode mode) => mode == AssistantMode.Freestyle
        ? "List the user's Freestyle collections and quizzes with ids. Use it to find a quiz or collection by name and to avoid creating duplicates."
        : "List the user's collections and quizzes for a language, with ids. Use it to find a quiz or collection by name and to avoid creating duplicates.";

    protected override async Task<ToolResult> RunAsync(ListLibraryArgs args, ToolContext context, CancellationToken cancellationToken)
    {
        var language = context.IsFreestyle
            ? QuizLanguageCatalog.FreestyleName
            : (string.IsNullOrWhiteSpace(args.Language) ? context.TargetLanguage : args.Language)?.Trim();
        if (string.IsNullOrWhiteSpace(language))
        {
            return ToolResult.Fail("Could not list the library", "language is required because no app language is selected.");
        }

        var collections = await db.Collections.AsNoTracking()
            .Where(collection => collection.UserId == context.UserId && collection.Language == language)
            .OrderBy(collection => collection.Name)
            .Select(collection => new
            {
                id = collection.Id,
                name = collection.Name,
                parent_collection_id = collection.ParentCollectionId,
                is_public = collection.IsPublic,
            })
            .ToListAsync(cancellationToken);
        var quizzes = db.Quizzes.AsNoTracking()
            .Where(quiz => quiz.UserId == context.UserId && (quiz.TargetLanguage == language || quiz.Language == language));
        var total = await quizzes.CountAsync(cancellationToken);
        var rows = await quizzes
            .OrderBy(quiz => quiz.Name)
            .Take(MaximumQuizzes)
            .Select(quiz => new
            {
                id = quiz.Id,
                name = quiz.Name,
                source_language = quiz.SourceLanguage,
                target_language = quiz.TargetLanguage,
                collection_id = quiz.CollectionId,
                is_public = quiz.IsPublic,
            })
            .ToListAsync(cancellationToken);
        return ToolResult.Ok(
            $"Listed {QuizContent.Count(rows.Count, "quiz")} and {QuizContent.Count(collections.Count, "collection")}",
            new
            {
                language,
                collections,
                quizzes = rows,
                quiz_count = total,
                quizzes_truncated = total > rows.Count,
                current_quiz_id = context.QuizId,
            });
    }
}
