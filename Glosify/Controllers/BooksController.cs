using Glosify.Extensions;
using Glosify.Models;
using Glosify.Models.Library;
using Glosify.Models.ViewModels;
using Glosify.Services;
using Glosify.Services.Books;
using Glosify.Services.Quizzes;
using Glosify.Services.Ai;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[Authorize]
public sealed class BooksController : Controller
{
    private readonly IBookDocumentService _books;
    private readonly IQuizService _quizzes;
    private readonly ILogger<BooksController> _logger;
    private readonly IPaidServiceGate _paidServices;

    public BooksController(
        IBookDocumentService books,
        IQuizService quizzes,
        ILogger<BooksController> logger,
        IPaidServiceGate paidServices)
    {
        _books = books;
        _quizzes = quizzes;
        _logger = logger;
        _paidServices = paidServices;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        return View(new BookLibraryViewModel
        {
            Books = (await _books.GetUserBooksAsync(userId, cancellationToken))
                .Select(BookCard.From)
                .ToList()
        });
    }

    [HttpPost]
    [RequestSizeLimit(26 * 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile? file, CancellationToken cancellationToken)
    {
        var paidStatus = await _paidServices.GetStatusAsync(cancellationToken);
        if (!paidStatus.Available)
        {
            TempData[NotificationKeys.Book] = PaidServicesUnavailableMessage(
                paidStatus.Reason,
                paidStatus.ResetsAtUtc);
            return RedirectToAction(nameof(Index));
        }

        var userId = User.GetUserId();
        if (file is null)
        {
            TempData[NotificationKeys.Book] = "Choose a PDF file to upload.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            var document = await _books.UploadAsync(userId, file, cancellationToken);
            TempData[NotificationKeys.Book] = $"Uploaded {document.Title}.";
            return RedirectToAction(nameof(Read), new { id = document.Id });
        }
        catch (ArgumentException ex)
        {
            TempData[NotificationKeys.Book] = ex.Message;
            return RedirectToAction(nameof(Index));
        }
        catch (PaidServicesBudgetExhaustedException ex)
        {
            TempData[NotificationKeys.Book] = PaidServicesUnavailableMessage(ex.Reason, ex.ResetsAtUtc);
            return RedirectToAction(nameof(Index));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Book upload failed for user {UserId}", userId);
            TempData[NotificationKeys.Book] = "The PDF could not be processed. Try a text-based PDF.";
            return RedirectToAction(nameof(Index));
        }
    }

    private static string PaidServicesUnavailableMessage(string? reason, DateTimeOffset resetsAtUtc) =>
        $"{reason ?? PaidServiceGate.BudgetExhaustedReason} Paid features reopen at {resetsAtUtc:yyyy-MM-dd HH:mm} UTC.";

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var book = await _books.GetOwnedDocumentAsync(id, userId, cancellationToken);
        if (book is null)
        {
            return NotFound();
        }

        if (!await _books.DeleteAsync(id, userId, cancellationToken))
        {
            return NotFound();
        }

        TempData[NotificationKeys.Book] = $"Deleted {book.Title}.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Read(Guid id, Guid? quizId, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var book = await _books.GetOwnedDocumentAsync(id, userId, cancellationToken);
        if (book == null)
        {
            return NotFound();
        }

        var quizzes = await _quizzes.GetUserQuizzesAsync(userId, cancellationToken);
        var selectedQuizId = quizId.HasValue && quizzes.Any(quiz => quiz.Id == quizId.Value)
            ? quizId
            : null;

        return View(new BookReaderViewModel
        {
            Book = BookCard.From(book),
            Quizzes = quizzes.Select(QuizCard.From).ToList(),
            SelectedQuizId = selectedQuizId
        });
    }

    [HttpGet]
    public async Task<IActionResult> File(Guid id, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        try
        {
            var stream = await _books.OpenOwnedPdfAsync(id, userId, cancellationToken);
            return File(stream, "application/pdf", enableRangeProcessing: true);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet]
    public async Task<IActionResult> Page(Guid id, int pageNumber, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var page = await _books.GetOwnedPageAsync(id, pageNumber, userId, cancellationToken);
        if (page == null)
        {
            return NotFound();
        }

        return Json(new
        {
            page.PageNumber,
            page.Text,
            page.ExtractionWarning
        });
    }
}
