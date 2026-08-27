using Glosify.Models.Library;

namespace Glosify.Services.Books;

public interface IBookDocumentService
{
    Task<IReadOnlyList<BookDocument>> GetUserBooksAsync(string userId, CancellationToken cancellationToken = default);
    Task<BookDocument> UploadAsync(string userId, IFormFile file, CancellationToken cancellationToken = default);
    Task<BookDocument?> GetOwnedDocumentAsync(Guid id, string userId, CancellationToken cancellationToken = default);
    Task<BookPage?> GetOwnedPageAsync(Guid documentId, int pageNumber, string userId, CancellationToken cancellationToken = default);
    Task<Stream> OpenOwnedPdfAsync(Guid documentId, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently removes a book, its extracted pages, and their cached translations.
    /// Assistant chats pointing at it are detached rather than deleted. Returns false
    /// when the book does not exist or belongs to someone else.
    /// </summary>
    Task<bool> DeleteAsync(Guid documentId, string userId, CancellationToken cancellationToken = default);
}
