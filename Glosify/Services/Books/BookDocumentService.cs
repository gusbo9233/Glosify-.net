using Glosify.Data;
using Glosify.Models.Library;
using Glosify.Services.Language;
using Glosify.Services.Storage;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Books;

public sealed class BookDocumentService : IBookDocumentService
{
    private const long MaxPdfBytes = 25 * 1024 * 1024;

    private readonly GlosifyContext _context;
    private readonly IBookFileStorage _storage;
    private readonly IPdfTextExtractionService _pdfTextExtraction;
    private readonly ILanguageContext _languageContext;
    private readonly ILogger<BookDocumentService> _logger;

    public BookDocumentService(
        GlosifyContext context,
        IBookFileStorage storage,
        IPdfTextExtractionService pdfTextExtraction,
        ILanguageContext languageContext,
        ILogger<BookDocumentService> logger)
    {
        _context = context;
        _storage = storage;
        _pdfTextExtraction = pdfTextExtraction;
        _languageContext = languageContext;
        _logger = logger;
    }

    public async Task<IReadOnlyList<BookDocument>> GetUserBooksAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var query = _context.BookDocuments
            .AsNoTracking()
            .Where(book => book.UserId == userId);

        var language = _languageContext.CurrentLanguage;
        if (!string.IsNullOrWhiteSpace(language))
        {
            query = query.Where(book => book.Language == language);
        }

        return await query
            .OrderByDescending(book => book.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<BookDocument> UploadAsync(
        string userId,
        IFormFile file,
        CancellationToken cancellationToken = default)
    {
        ValidatePdf(file);

        var documentId = Guid.NewGuid();
        var blobName = $"users/{userId}/books/{documentId}.pdf";
        var now = DateTimeOffset.UtcNow;

        // ASP.NET has already buffered the form file (memory or temp file), so each
        // OpenReadStream call is an independent seekable view; copying the whole PDF
        // into a MemoryStream here just doubled the memory cost of an upload.
        IReadOnlyList<ExtractedPdfPage> pages;
        try
        {
            await using (var uploadStream = file.OpenReadStream())
            {
                await _storage.UploadAsync(uploadStream, blobName, "application/pdf", cancellationToken);
            }

            await using (var extractionStream = file.OpenReadStream())
            {
                pages = await _pdfTextExtraction.ExtractPagesAsync(extractionStream, cancellationToken);
            }
        }
        catch
        {
            await TryDeleteUploadedBlobAsync(blobName);
            throw;
        }

        var document = new BookDocument
        {
            Id = documentId,
            UserId = userId,
            Title = Path.GetFileNameWithoutExtension(file.FileName).Trim(),
            OriginalFileName = Path.GetFileName(file.FileName),
            BlobName = blobName,
            Language = _languageContext.CurrentLanguage,
            PageCount = pages.Count,
            ProcessingStatus = "Ready",
            CreatedAt = now,
            UpdatedAt = now,
            Pages = pages.Select(page => new BookPage
            {
                Id = Guid.NewGuid(),
                BookDocumentId = documentId,
                PageNumber = page.PageNumber,
                Text = page.Text,
                ExtractionWarning = page.Warning,
            }).ToList()
        };

        if (string.IsNullOrWhiteSpace(document.Title))
        {
            document.Title = "Untitled book";
        }

        try
        {
            _context.BookDocuments.Add(document);
            await _context.SaveChangesAsync(cancellationToken);
            return document;
        }
        catch
        {
            // Blob storage and SQL cannot share a transaction. Compensate the completed
            // upload when persistence fails so an invisible orphan is not left behind.
            await TryDeleteUploadedBlobAsync(blobName);
            throw;
        }
    }

    public async Task<BookDocument?> GetOwnedDocumentAsync(
        Guid id,
        string userId,
        CancellationToken cancellationToken = default)
    {
        return await _context.BookDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(book => book.Id == id && book.UserId == userId, cancellationToken);
    }

    public async Task<BookPage?> GetOwnedPageAsync(
        Guid documentId,
        int pageNumber,
        string userId,
        CancellationToken cancellationToken = default)
    {
        return await _context.BookPages
            .AsNoTracking()
            .Include(page => page.BookDocument)
            .FirstOrDefaultAsync(
                page => page.BookDocumentId == documentId
                    && page.PageNumber == pageNumber
                    && page.BookDocument.UserId == userId,
                cancellationToken);
    }

    public async Task<Stream> OpenOwnedPdfAsync(
        Guid documentId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var document = await GetOwnedDocumentAsync(documentId, userId, cancellationToken)
            ?? throw new FileNotFoundException("Book not found.");

        return await _storage.OpenReadAsync(document.BlobName, cancellationToken);
    }

    public async Task<bool> DeleteAsync(
        Guid documentId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var document = await _context.BookDocuments
            .FirstOrDefaultAsync(book => book.Id == documentId && book.UserId == userId, cancellationToken);
        if (document is null)
        {
            return false;
        }

        // assistant_threads.context_book_document_id is NO ACTION, so a chat still
        // pointing at this book would block the delete. Detach those chats instead of
        // deleting them: the conversation is worth keeping without its source material.
        var threads = await _context.AssistantThreads
            .Where(thread => thread.ContextBookDocumentId == documentId)
            .ToListAsync(cancellationToken);
        foreach (var thread in threads)
        {
            thread.ContextBookDocumentId = null;
        }

        // Pages and their cached translations cascade from the document.
        _context.BookDocuments.Remove(document);
        await _context.SaveChangesAsync(cancellationToken);

        // Deliberately after the commit. A leftover blob is invisible and merely costs
        // storage, whereas a row surviving a deleted blob breaks the reader for a book
        // the user can still see.
        try
        {
            await _storage.DeleteIfExistsAsync(document.BlobName, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Deleted book {DocumentId} but could not remove its blob {BlobName}.",
                documentId,
                document.BlobName);
        }

        return true;
    }

    private static void ValidatePdf(IFormFile? file)
    {
        if (file is null || file.Length == 0)
        {
            throw new ArgumentException("Choose a PDF file to upload.");
        }

        if (file.Length > MaxPdfBytes)
        {
            throw new ArgumentException("Choose a PDF under 25 MB.");
        }

        var hasPdfExtension = string.Equals(
            Path.GetExtension(file.FileName),
            ".pdf",
            StringComparison.OrdinalIgnoreCase);
        var hasPdfContentType = string.Equals(
            file.ContentType,
            "application/pdf",
            StringComparison.OrdinalIgnoreCase);

        if (!hasPdfExtension && !hasPdfContentType)
        {
            throw new ArgumentException("Only PDF files can be uploaded.");
        }
    }

    private async Task TryDeleteUploadedBlobAsync(string blobName)
    {
        try
        {
            await _storage.DeleteIfExistsAsync(blobName, CancellationToken.None);
        }
        catch (Exception cleanupException)
        {
            _logger.LogWarning(
                cleanupException,
                "Could not compensate failed book upload by deleting blob {BlobName}.",
                blobName);
        }
    }
}
