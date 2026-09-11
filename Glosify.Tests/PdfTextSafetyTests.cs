using System.Text;
using Glosify.Services.Books;
using Xunit;

namespace Glosify.Tests;

public sealed class PdfTextSafetyTests
{
    [Theory]
    [InlineData(1001, "short")]
    [InlineData(1, "oversized")]
    public async Task WorkerRejectsPageAndExtractedTextLimits(int pages, string input)
    {
        var text = input == "oversized" ? new string('x', 50_001) : input;
        var directory = Path.Combine(Path.GetTempPath(), "glosify-pdf-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "input.pdf"); var output = Path.Combine(directory, "output.json");
            await File.WriteAllBytesAsync(source, Pdf(pages, text));
            Assert.Equal(1, await IsolatedPdfTextExtractionService.RunWorkerAsync(["--extract-pdf", source, output]));
            Assert.False(File.Exists(output));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ParserExtractsRealText_AndWorkerRejectsInvalidInput()
    {
        using var stream = new MemoryStream(Pdf(1, "Hello worker"));
        var page = Assert.Single(await new PdfPigTextExtractionService().ExtractPagesAsync(stream));
        Assert.Equal(1, page.PageNumber); Assert.Equal("Hello worker", page.Text); Assert.Null(page.Warning);
        var directory = Path.Combine(Path.GetTempPath(), "glosify-pdf-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "invalid.pdf"); var output = Path.Combine(directory, "output.json");
            await File.WriteAllTextAsync(source, "not a PDF");
            Assert.Equal(1, await IsolatedPdfTextExtractionService.RunWorkerAsync(["--extract-pdf", source, output]));
            Assert.False(File.Exists(output));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static byte[] Pdf(int pages, string text)
    {
        var fontId = pages + 3; var streamId = pages + 4;
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(3, pages).Select(i => $"{i} 0 R"))}] /Count {pages} >>" };
        objects.AddRange(Enumerable.Range(0, pages).Select(_ => $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Resources << /Font << /F1 {fontId} 0 R >> >> /Contents {streamId} 0 R >>"));
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var content = $"BT /F1 12 Tf 20 100 Td ({text}) Tj ET";
        objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        var pdf = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        foreach (var value in objects) { offsets.Add(pdf.Length); pdf.Append($"{offsets.Count} 0 obj\n{value}\nendobj\n"); }
        var xref = pdf.Length; pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
