using Glosify.Models.Library;

namespace Glosify.Services.Books;

public interface IBookDocumentService
{
    Task<IReadOnlyList<BookDocument>> GetUserBooksAsync(string userId, CancellationToken cancellationToken = default);
    Task<BookDocument> UploadAsync(string userId, IFormFile file, CancellationToken cancellationToken = default);
    Task<BookDocument?> GetOwnedDocumentAsync(Guid id, string userId, CancellationToken cancellationToken = default);
    Task<BookPage?> GetOwnedPageAsync(Guid documentId, int pageNumber, string userId, CancellationToken cancellationToken = default);
    Task<Stream> OpenOwnedPdfAsync(Guid documentId, string userId, CancellationToken cancellationToken = default);
}
