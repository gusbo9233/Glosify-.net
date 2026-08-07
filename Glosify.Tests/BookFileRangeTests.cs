using System.Security.Claims;
using Glosify.Controllers;
using Glosify.Models.Library;
using Glosify.Services.Books;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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

    private static BooksController CreateController(Stream pdf)
    {
        var controller = new BooksController(
            new StubBookDocumentService(pdf),
            quizzes: null!, // The File action never reaches the quiz service.
            NullLogger<BooksController>.Instance);

        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())],
            authenticationType: "Test"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };

        return controller;
    }

    private sealed class StubBookDocumentService(Stream pdf) : IBookDocumentService
    {
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
