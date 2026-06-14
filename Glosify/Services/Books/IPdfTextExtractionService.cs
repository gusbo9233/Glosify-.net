namespace Glosify.Services.Books;

public interface IPdfTextExtractionService
{
    Task<IReadOnlyList<ExtractedPdfPage>> ExtractPagesAsync(Stream pdf, CancellationToken cancellationToken = default);
}
