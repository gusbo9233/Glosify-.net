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
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var text = (page.Text ?? string.Empty).Trim();
            pages.Add(new ExtractedPdfPage(
                page.Number,
                text,
                string.IsNullOrWhiteSpace(text) ? EmptyPageWarning : null));
        }

        return Task.FromResult<IReadOnlyList<ExtractedPdfPage>>(pages);
    }
}
