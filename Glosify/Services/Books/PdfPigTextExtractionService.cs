using UglyToad.PdfPig;

namespace Glosify.Services.Books;

public sealed class PdfPigTextExtractionService : IPdfTextExtractionService
{
    private const string EmptyPageWarning = "No selectable text found on this page.";

    public Task<IReadOnlyList<ExtractedPdfPage>> ExtractPagesAsync(
        Stream pdf,
        CancellationToken cancellationToken = default)
    {
        if (pdf is null)
        {
            throw new ArgumentNullException(nameof(pdf));
        }

        if (pdf.CanSeek)
        {
            pdf.Position = 0;
        }

        var pages = new List<ExtractedPdfPage>();
        using var document = PdfDocument.Open(pdf);
        if (document.IsEncrypted || document.NumberOfPages > 1000) throw new ArgumentException("The PDF is encrypted or exceeds 1,000 pages.");
        var totalCharacters = 0;
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var text = page.Text ?? string.Empty;
            totalCharacters += text.Length;
            if (text.Length > 50_000 || totalCharacters > 5_000_000) throw new ArgumentException("PDF text exceeds its processing limit.");
            pages.Add(new ExtractedPdfPage(
                page.Number,
                text.Trim(),
                string.IsNullOrWhiteSpace(text) ? EmptyPageWarning : null));
        }

        return Task.FromResult<IReadOnlyList<ExtractedPdfPage>>(pages);
    }
}
