using Glosify.Models.Api;
using Glosify.Models.Library;
using Glosify.Services;
using Glosify.Services.Books;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers.Api;

/// <summary>
/// Book library endpoints for the mobile app, mirroring BooksController.
/// </summary>
[Route("api/books")]
public class BooksApiController : ApiControllerBase
{
    private readonly IBookDocumentService _books;
    private readonly ILogger<BooksApiController> _logger;

    public BooksApiController(IBookDocumentService books, ILogger<BooksApiController> logger)
    {
        _books = books;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<BookDto>>> List(CancellationToken cancellationToken)
    {
        var books = await _books.GetUserBooksAsync(User.GetUserId(), cancellationToken);
        return Ok(books.Select(BookDto.From).ToList());
    }

    [HttpPost]
    [RequestSizeLimit(26 * 1024 * 1024)]
    public async Task<ActionResult<BookDto>> Upload(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return BadRequest("Choose a PDF file to upload.");
        }

        try
        {
            var document = await _books.UploadAsync(User.GetUserId(), file, cancellationToken);
            return Ok(BookDto.From(document));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Book upload failed for user {UserId}", User.GetUserId());
            return UnprocessableEntity("The PDF could not be processed. Try a text-based PDF.");
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<BookDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var book = await _books.GetOwnedDocumentAsync(id, User.GetUserId(), cancellationToken);
        return book == null ? NotFound() : Ok(BookDto.From(book));
    }

    [HttpGet("{id:guid}/pages/{pageNumber:int}")]
    public async Task<ActionResult<BookPageDto>> Page(Guid id, int pageNumber, CancellationToken cancellationToken)
    {
        var page = await _books.GetOwnedPageAsync(id, pageNumber, User.GetUserId(), cancellationToken);
        return page == null
            ? NotFound()
            : Ok(new BookPageDto(page.PageNumber, page.Text, page.ExtractionWarning));
    }

    [HttpGet("{id:guid}/file")]
    public async Task<IActionResult> File(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var stream = await _books.OpenOwnedPdfAsync(id, User.GetUserId(), cancellationToken);
            return File(stream, "application/pdf", enableRangeProcessing: true);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
    }
}
