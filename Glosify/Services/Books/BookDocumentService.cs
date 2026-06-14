using Glosify.Data;
using Glosify.Models.Library;
using Glosify.Services.Storage;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Books;

public sealed class BookDocumentService : IBookDocumentService
{
    private const long MaxPdfBytes = 25 * 1024 * 1024;

    private readonly GlosifyContext _context;
    private readonly IBookFileStorage _storage;
    private readonly IPdfTextExtractionService _pdfTextExtraction;

    public BookDocumentService(
        GlosifyContext context,
        IBookFileStorage storage,
        IPdfTextExtractionService pdfTextExtraction)
    {
        _context = context;
        _storage = storage;
        _pdfTextExtraction = pdfTextExtraction;
    }

    public async Task<IReadOnlyList<BookDocument>> GetUserBooksAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        return await _context.BookDocuments
            .Where(book => book.UserId == userId)
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
        await using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellationToken);

        IReadOnlyList<ExtractedPdfPage> pages;
        try
        {
            buffer.Position = 0;
            await _storage.UploadAsync(buffer, blobName, "application/pdf", cancellationToken);

            buffer.Position = 0;
            pages = await _pdfTextExtraction.ExtractPagesAsync(buffer, cancellationToken);
        }
        catch
        {
            await _storage.DeleteIfExistsAsync(blobName, CancellationToken.None);
            throw;
        }

        var document = new BookDocument
        {
            Id = documentId,
            UserId = userId,
            Title = Path.GetFileNameWithoutExtension(file.FileName).Trim(),
            OriginalFileName = Path.GetFileName(file.FileName),
            BlobName = blobName,
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

        _context.BookDocuments.Add(document);
        await _context.SaveChangesAsync(cancellationToken);
        return document;
    }

    public async Task<BookDocument?> GetOwnedDocumentAsync(
        Guid id,
        string userId,
        CancellationToken cancellationToken = default)
    {
        return await _context.BookDocuments
            .FirstOrDefaultAsync(book => book.Id == id && book.UserId == userId, cancellationToken);
    }

    public async Task<BookPage?> GetOwnedPageAsync(
        Guid documentId,
        int pageNumber,
        string userId,
        CancellationToken cancellationToken = default)
    {
        return await _context.BookPages
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
}
