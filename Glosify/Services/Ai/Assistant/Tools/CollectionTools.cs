using System.ComponentModel;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Language;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed record CreateCollectionArgs(
    [property: Description("Collection name.")] string Name,
    [property: Description("Id of the parent collection. Null for the library root.")] string? ParentCollectionId = null);

internal sealed class CreateCollectionTool(GlosifyContext db) : AssistantTool<CreateCollectionArgs>
{
    public override string Name => "create_collection";

    public override AssistantToolKind Kind => AssistantToolKind.Write;

    protected override string Describe(AssistantMode mode) =>
        "Create a collection in the current language for grouping quizzes. Saved immediately; the user can undo. Check list_library first to avoid duplicates.";

    protected override async Task<ToolResult> RunAsync(CreateCollectionArgs args, ToolContext context, CancellationToken cancellationToken)
    {
        const string title = "Could not create the collection";
        var language = context.IsFreestyle ? QuizLanguageCatalog.FreestyleName : context.TargetLanguage;
        if (string.IsNullOrWhiteSpace(args.Name))
        {
            return ToolResult.Fail(title, "name is required.");
        }

        if (string.IsNullOrWhiteSpace(language))
        {
            return ToolResult.Fail(title, "No app language is selected. Ask the user to choose one first.");
        }

        Guid? parent = null;
        if (!string.IsNullOrWhiteSpace(args.ParentCollectionId))
        {
            if (!Guid.TryParse(args.ParentCollectionId, out var parsed)
                || !await db.Collections.AnyAsync(
                    collection => collection.Id == parsed && collection.UserId == context.UserId && collection.Language == language,
                    cancellationToken))
            {
                return ToolResult.Fail(title, "The parent collection was not found. Use list_library to find collection ids.");
            }

            parent = parsed;
        }

        var name = args.Name.Trim();
        if (await db.Collections.AnyAsync(
                collection => collection.UserId == context.UserId
                    && collection.Language == language
                    && collection.ParentCollectionId == parent
                    && collection.Name == name,
                cancellationToken))
        {
            return ToolResult.Fail(title, "A collection with that name already exists there.");
        }

        return ToolResult.Propose(
            $"Create collection “{name}”",
            null,
            [QuizContent.Change(PendingChangeKinds.CreateCollection, new
            {
                kind = PendingChangeKinds.CreateCollection,
                name,
                language,
                parent_collection_id = parent,
            })]);
    }
}

internal sealed record RenameCollectionArgs(
    [property: Description("Id of the collection, from list_library.")] string CollectionId,
    [property: Description("New collection name.")] string Name);

internal sealed class RenameCollectionTool(GlosifyContext db) : AssistantTool<RenameCollectionArgs>
{
    public override string Name => "rename_collection";

    public override AssistantToolKind Kind => AssistantToolKind.Write;

    protected override string Describe(AssistantMode mode) =>
        "Rename one of the user's collections. Saved immediately; the user can undo.";

    protected override async Task<ToolResult> RunAsync(RenameCollectionArgs args, ToolContext context, CancellationToken cancellationToken)
    {
        const string title = "Could not rename the collection";
        var name = args.Name.Trim();
        if (name.Length == 0)
        {
            return ToolResult.Fail(title, "name is required.");
        }

        var collection = await CollectionRules.FindAsync(db, context, args.CollectionId, cancellationToken);
        if (collection is null)
        {
            return ToolResult.Fail(title, "Collection not found. Use list_library to find collection ids.");
        }

        if (collection.Name == name)
        {
            return ToolResult.Fail(title, "The collection already has that name.");
        }

        if (await db.Collections.AnyAsync(
                other => other.Id != collection.Id
                    && other.UserId == context.UserId
                    && other.Language == collection.Language
                    && other.ParentCollectionId == collection.ParentCollectionId
                    && other.Name == name,
                cancellationToken))
        {
            return ToolResult.Fail(title, "A collection with that name already exists in the same place.");
        }

        return ToolResult.Propose(
            $"Rename collection “{collection.Name}” to “{name}”",
            null,
            [QuizContent.Change(PendingChangeKinds.RenameCollection, new
            {
                kind = PendingChangeKinds.RenameCollection,
                collection_id = collection.Id,
                original_name = collection.Name,
                name,
            })]);
    }
}

internal sealed record MoveQuizArgs(
    [property: Description("Id of the quiz to move, from list_library.")] string QuizId,
    [property: Description("Id of the destination collection. Null for the library root.")] string? CollectionId = null);

/// <summary>Moves a quiz between collections. Moving can change who can see a quiz, so it waits for approval.</summary>
internal sealed class MoveQuizTool(GlosifyContext db) : AssistantTool<MoveQuizArgs>
{
    public override string Name => "move_quiz";

    public override AssistantToolKind Kind => AssistantToolKind.Write;

    protected override string Describe(AssistantMode mode) =>
        "Move one of the user's quizzes into a collection, or to the library root. The user is asked to approve moves before they happen.";

    protected override async Task<ToolResult> RunAsync(MoveQuizArgs args, ToolContext context, CancellationToken cancellationToken)
    {
        const string title = "Could not move the quiz";
        if (!Guid.TryParse(args.QuizId, out var quizId))
        {
            return ToolResult.Fail(title, "quiz_id must be a quiz id from list_library.");
        }

        var quiz = await db.Quizzes.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == quizId && candidate.UserId == context.UserId, cancellationToken);
        if (quiz is null)
        {
            return ToolResult.Fail(title, "Quiz not found. Use list_library to find quiz ids.");
        }

        Collection? destination = null;
        if (!string.IsNullOrWhiteSpace(args.CollectionId))
        {
            destination = await CollectionRules.FindAsync(db, context, args.CollectionId, cancellationToken);
            if (destination is null || destination.Language != quiz.TargetLanguage)
            {
                return ToolResult.Fail(title, "The destination collection was not found for this quiz's language.");
            }
        }

        if (quiz.CollectionId == destination?.Id)
        {
            return ToolResult.Fail(title, "The quiz is already there.");
        }

        return ToolResult.Propose(
            destination is null ? $"Move quiz “{quiz.Name}” to the library root" : $"Move quiz “{quiz.Name}” to “{destination.Name}”",
            null,
            [QuizContent.Change(PendingChangeKinds.MoveQuiz, new
            {
                kind = PendingChangeKinds.MoveQuiz,
                quiz_id = quiz.Id,
                quiz_name = quiz.Name,
                collection_id = destination?.Id,
                collection_name = destination?.Name,
            })]);
    }
}

internal sealed record MoveCollectionArgs(
    [property: Description("Id of the collection to move, from list_library.")] string CollectionId,
    [property: Description("Id of the new parent collection. Null for the library root.")] string? ParentCollectionId = null);

internal sealed class MoveCollectionTool(GlosifyContext db) : AssistantTool<MoveCollectionArgs>
{
    public override string Name => "move_collection";

    public override AssistantToolKind Kind => AssistantToolKind.Write;

    protected override string Describe(AssistantMode mode) =>
        "Move a collection under another collection, or to the library root. The user is asked to approve moves before they happen.";

    protected override async Task<ToolResult> RunAsync(MoveCollectionArgs args, ToolContext context, CancellationToken cancellationToken)
    {
        const string title = "Could not move the collection";
        var collection = await CollectionRules.FindAsync(db, context, args.CollectionId, cancellationToken);
        if (collection is null)
        {
            return ToolResult.Fail(title, "Collection not found. Use list_library to find collection ids.");
        }

        Collection? parent = null;
        if (!string.IsNullOrWhiteSpace(args.ParentCollectionId))
        {
            parent = await CollectionRules.FindAsync(db, context, args.ParentCollectionId, cancellationToken);
            if (parent is null || parent.Language != collection.Language)
            {
                return ToolResult.Fail(title, "The destination collection was not found for this language.");
            }

            if (await CollectionRules.IsWithinAsync(db, context.UserId, collection, parent.Id, cancellationToken))
            {
                return ToolResult.Fail(title, "A collection cannot be moved inside itself or one of its descendants.");
            }
        }

        var destinationId = parent?.Id;
        if (collection.ParentCollectionId == destinationId)
        {
            return ToolResult.Fail(title, "The collection is already there.");
        }

        if (await db.Collections.AnyAsync(
                other => other.Id != collection.Id
                    && other.UserId == context.UserId
                    && other.Language == collection.Language
                    && other.ParentCollectionId == destinationId
                    && other.Name == collection.Name,
                cancellationToken))
        {
            return ToolResult.Fail(title, "A collection with that name already exists in the destination.");
        }

        return ToolResult.Propose(
            parent is null
                ? $"Move collection “{collection.Name}” to the library root"
                : $"Move collection “{collection.Name}” under “{parent.Name}”",
            null,
            [QuizContent.Change(PendingChangeKinds.MoveCollection, new
            {
                kind = PendingChangeKinds.MoveCollection,
                collection_id = collection.Id,
                collection_name = collection.Name,
                parent_collection_id = parent?.Id,
                parent_collection_name = parent?.Name,
            })]);
    }
}

internal static class CollectionRules
{
    public static async Task<Collection?> FindAsync(
        GlosifyContext db,
        ToolContext context,
        string? id,
        CancellationToken cancellationToken) =>
        Guid.TryParse(id, out var parsed)
            ? await db.Collections.AsNoTracking()
                .FirstOrDefaultAsync(collection => collection.Id == parsed && collection.UserId == context.UserId, cancellationToken)
            : null;

    /// <summary>Whether <paramref name="candidateId"/> is the collection itself or sits below it.</summary>
    public static async Task<bool> IsWithinAsync(
        GlosifyContext db,
        string userId,
        Collection collection,
        Guid candidateId,
        CancellationToken cancellationToken)
    {
        var parents = await db.Collections.AsNoTracking()
            .Where(other => other.UserId == userId && other.Language == collection.Language)
            .ToDictionaryAsync(other => other.Id, other => other.ParentCollectionId, cancellationToken);
        var visited = new HashSet<Guid>();
        Guid? current = candidateId;
        while (current is Guid id)
        {
            if (id == collection.Id || !visited.Add(id))
            {
                return true;
            }

            current = parents.GetValueOrDefault(id);
        }

        return false;
    }
}
