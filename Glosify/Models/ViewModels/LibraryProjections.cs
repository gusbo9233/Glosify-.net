using Glosify.Models.Entities;
using Glosify.Models.Library;
using Glosify.Services.Language;

namespace Glosify.Models.ViewModels;

/// <summary>
/// The library entities as the pages that show them need them.
/// </summary>
/// <remarks>
/// These finish the job the classroom projections started: no view model above the service
/// layer holds a tracked EF entity. Property names deliberately match the entities they
/// replace, so most Razor reads unchanged — the point is what a view can no longer do,
/// which is walk a navigation property and trigger a load mid-render.
/// </remarks>
public sealed record QuizCard(
    Guid Id,
    string Name,
    string SourceLanguage,
    string TargetLanguage,
    string ProcessingStatus,
    DateTimeOffset CreatedAt,
    Guid? CollectionId,
    bool IsPublic)
{
    public bool IsFreestyle => QuizLanguageCatalog.IsFreestyle(TargetLanguage);

    public static QuizCard From(Quiz quiz) => new(
        quiz.Id,
        quiz.Name,
        quiz.SourceLanguage,
        quiz.TargetLanguage,
        quiz.ProcessingStatus,
        quiz.CreatedAt,
        quiz.CollectionId,
        quiz.IsPublic);
}

public sealed record CollectionCard(
    Guid Id,
    string Name,
    string Language,
    Guid? ParentCollectionId,
    DateTimeOffset CreatedAt,
    bool IsPublic)
{
    public bool IsFreestyle => QuizLanguageCatalog.IsFreestyle(Language);

    public static CollectionCard From(Collection collection) => new(
        collection.Id,
        collection.Name,
        collection.Language,
        collection.ParentCollectionId,
        collection.CreatedAt,
        collection.IsPublic);
}

public sealed record WordRow(string Id, string Lemma, string Translation)
{
    public static WordRow From(Word word) => new(word.Id, word.Lemma, word.Translation);
}

public sealed record BookCard(
    Guid Id,
    string Title,
    int PageCount,
    string? PreferredTranslationLanguage,
    string ProcessingStatus,
    string? ProcessingMessage,
    DateTimeOffset CreatedAt)
{
    public static BookCard From(BookDocument book) => new(
        book.Id,
        book.Title,
        book.PageCount,
        book.PreferredTranslationLanguage,
        book.ProcessingStatus,
        book.ProcessingMessage,
        book.CreatedAt);
}

/// <summary>
/// One node of a public collection tree, with its breadcrumb already resolved.
/// </summary>
/// <remarks>
/// Explore/Collection.cshtml used to do this work itself, in an <c>@functions</c> block that
/// recursed through <c>ChildCollections</c> and walked <c>ParentCollection</c> upwards to
/// build each breadcrumb. That only worked because the controller had eagerly loaded the
/// whole graph; any query change would have turned it into a silent per-node round trip, or
/// a null-reference once the tracked graph was gone. The traversal happens once, here.
/// </remarks>
public sealed record ExploreCollectionNode(
    Guid Id,
    string Name,
    string Language,
    string Path,
    IReadOnlyList<QuizCard> Quizzes,
    IReadOnlyList<ExploreCollectionNode> ChildCollections)
{
    /// <summary>Every descendant, depth-first and name-ordered. Excludes this node.</summary>
    public IEnumerable<ExploreCollectionNode> Descendants() =>
        ChildCollections.SelectMany(child => new[] { child }.Concat(child.Descendants()));

    public int DescendantCollectionCount => Descendants().Count();

    public int TotalQuizCount => Quizzes.Count + Descendants().Sum(node => node.Quizzes.Count);

    /// <summary>
    /// Projects a loaded collection graph. <paramref name="ancestors"/> accumulates the
    /// breadcrumb on the way down, which is why no node needs a parent backreference.
    /// </summary>
    public static ExploreCollectionNode From(Collection collection, IReadOnlyList<string>? ancestors = null)
    {
        ancestors ??= [];
        var path = ancestors.Count == 0 ? collection.Language : string.Join(" / ", ancestors);
        var childAncestors = ancestors.Append(collection.Name).ToArray();

        return new ExploreCollectionNode(
            collection.Id,
            collection.Name,
            collection.Language,
            path,
            collection.Quizzes.OrderBy(quiz => quiz.Name, StringComparer.CurrentCulture)
                .Select(QuizCard.From)
                .ToArray(),
            collection.ChildCollections.OrderBy(child => child.Name, StringComparer.CurrentCulture)
                .Select(child => From(child, childAncestors))
                .ToArray());
    }
}

/// <summary>
/// One row of the AI credit admin ledger.
/// </summary>
/// <remarks>
/// The admin page showed six of <see cref="AiCreditTransaction"/>'s twenty-four columns and
/// had the other eighteen — including the running balance and every token breakdown — one
/// keystroke away in the view.
/// </remarks>
public sealed record AiCreditTransactionRow(
    string Kind,
    int CreditAmount,
    string? Feature,
    int? TotalTokens,
    string? Note,
    DateTimeOffset CreatedAt)
{
    public static AiCreditTransactionRow From(AiCreditTransaction transaction) => new(
        transaction.Kind,
        transaction.CreditAmount,
        transaction.Feature,
        transaction.TotalTokens,
        transaction.Note,
        transaction.CreatedAt);
}
