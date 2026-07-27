namespace Glosify.Services.Books;

public interface IBookPageTranslationService
{
    Task<string> UpdatePreferredLanguageAsync(
        Guid documentId,
        string userId,
        string? targetLanguage,
        CancellationToken cancellationToken = default);

    Task<BookPageTranslationResult> TranslatePageAsync(
        Guid documentId,
        int pageNumber,
        string userId,
        string? targetLanguage,
        IReadOnlyList<BookPageSourceSegment>? segments,
        CancellationToken cancellationToken = default);
}

public interface IBookPageTranslationCoordinator
{
    Task<IAsyncDisposable> AcquireAsync(string key, CancellationToken cancellationToken);
}
