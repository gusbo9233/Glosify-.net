using Glosify.Models.Entities;
using Glosify.Models.Library;
using Glosify.Services.Language;

namespace Glosify.Models.ViewModels;

/// <summary>
/// The library entities as the pages that show them need them.
/// </summary>
/// <remarks>
/// No view model above the service layer holds a tracked EF entity. Property names
/// deliberately match the entities they
/// replace, so most Razor reads unchanged — the point is what a view can no longer do,
/// which is walk a navigation property and trigger a load mid-render.
/// </remarks>
public sealed record QuizCard(
    Guid Id,
    string Name,
    string SourceLanguage,
    string TargetLanguage,
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
        Glosify.Services.Ai.CreditAmounts.Display(transaction.CreditAmount),
        transaction.Feature,
        transaction.TotalTokens,
        transaction.Note,
        transaction.CreatedAt);
}
