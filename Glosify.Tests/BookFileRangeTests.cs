using System.Security.Claims;
using Glosify.Controllers;
using Glosify.Models.Library;
using Glosify.Models;
using Glosify.Services.Ai;
using Glosify.Services.Books;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Glosify.Tests;

/// <summary>
/// The book shelf draws each cover by handing the PDF's URL to pdf.js, which
/// reads a few ranges out of a file that can run to several megabytes — but
/// only when the response supports ranges. It stopped doing so silently once,
/// and the library page began pulling 13 MB of PDFs to draw thumbnails.
/// </summary>
public sealed class BookFileRangeTests
{
    [Fact]
    public async Task Serving_a_book_supports_range_requests()
    {
        var pdf = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);
        var controller = CreateController(pdf);

        var result = await controller.File(Guid.NewGuid(), CancellationToken.None);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.True(
            file.EnableRangeProcessing,
            "Without range processing pdf.js downloads every byte of the book to render one page.");
        Assert.True(
            file.FileStream.CanSeek,
            "FileStreamResult ignores EnableRangeProcessing on a stream it cannot seek, so a "
            + "forward-only stream turns range support off without any error.");
    }

    [Fact]
    public void The_shelf_asks_pdfjs_for_page_one_rather_than_the_whole_book()
    {
        // Ranges on the endpoint buy nothing while pdf.js still prefetches and
        // streams the rest of the file, which is what it does by default.
        var shelf = File.ReadAllText(Path.Combine(WebRoot(), "js", "books-library.js"));

        Assert.Contains("disableAutoFetch: true", shelf, StringComparison.Ordinal);
        Assert.Contains("disableStream: true", shelf, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClosedPaidServiceGateRedirectsUploadWithAReadableMessage()
    {
        var resetsAtUtc = new DateTimeOffset(2026, 8, 31, 22, 0, 0, TimeSpan.Zero);
        var httpContext = new DefaultHttpContext();
        var controller = new BooksController(
            books: null!,
            quizzes: null!,
            NullLogger<BooksController>.Instance,
            new ClosedPaidServiceGate(resetsAtUtc))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new InMemoryTempDataProvider()),
        };

        var result = await controller.Upload(file: null, CancellationToken.None);

        Assert.Equal(nameof(BooksController.Index), Assert.IsType<RedirectToActionResult>(result).ActionName);
        var message = Assert.IsType<string>(controller.TempData[NotificationKeys.Book]);
        Assert.Contains("monthly application budget", message, StringComparison.Ordinal);
        Assert.Contains("2026-08-31 22:00 UTC", message, StringComparison.Ordinal);
    }

    private static string WebRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Glosify", "wwwroot");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the Glosify web root.");
    }

    private static BooksController CreateController(Stream pdf)
    {
        var controller = new BooksController(
            new StubBookDocumentService(pdf),
            quizzes: null!, // The File action never reaches the quiz service.
            NullLogger<BooksController>.Instance,
            new AlwaysAvailablePaidServiceGate());

        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())],
            authenticationType: "Test"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };

        return controller;
    }

    private sealed class ClosedPaidServiceGate(DateTimeOffset resetsAtUtc) : IPaidServiceGate
    {
        public Task<PaidServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PaidServiceStatus(false, PaidServiceGate.BudgetExhaustedReason, resetsAtUtc));

        public Task EnsureAvailableAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        private Dictionary<string, object> _values = [];

        public IDictionary<string, object> LoadTempData(HttpContext context) => _values;

        public void SaveTempData(HttpContext context, IDictionary<string, object> values) =>
            _values = new Dictionary<string, object>(values, StringComparer.Ordinal);
    }

    private sealed class StubBookDocumentService(Stream pdf) : IBookDocumentService
    {
        public Task<bool> DeleteAsync(Guid documentId, string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<Stream> OpenOwnedPdfAsync(Guid documentId, string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(pdf);

        public Task<IReadOnlyList<BookDocument>> GetUserBooksAsync(string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BookDocument> UploadAsync(string userId, IFormFile file, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BookDocument?> GetOwnedDocumentAsync(Guid id, string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BookPage?> GetOwnedPageAsync(Guid documentId, int pageNumber, string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> OpenPdfUncheckedAsync(Guid documentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
