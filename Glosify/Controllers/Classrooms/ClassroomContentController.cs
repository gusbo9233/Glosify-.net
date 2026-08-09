using Glosify.Services.Books;
using Glosify.Services.Classrooms;
using Glosify.Services.Quizzes;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers.Classrooms;

/// <summary>
/// The content tab: sharing quizzes and books into the classroom, and reading what has
/// been shared.
/// </summary>
public sealed class ClassroomContentController : ClassroomControllerBase
{
    private const string Tab = "content";

    private readonly IClassroomLibrary _library;
    private readonly IQuizService _quizzes;
    private readonly IBookDocumentService _books;

    public ClassroomContentController(
        IClassroomLibrary library,
        IQuizService quizzes,
        IBookDocumentService books,
        ILogger<ClassroomContentController> logger)
        : base(logger)
    {
        _library = library;
        _quizzes = quizzes;
        _books = books;
    }

    [HttpPost]
    public Task<IActionResult> ShareQuiz(Guid id, Guid quizId, CancellationToken cancellationToken)
        => RunAndReturnAsync(id, Tab, () => _library.ShareQuizAsync(id, User.GetUserId(), quizId, cancellationToken), "Quiz shared.");

    [HttpPost]
    public Task<IActionResult> ShareBook(Guid id, Guid bookId, CancellationToken cancellationToken)
        => RunAndReturnAsync(id, Tab, () => _library.ShareBookAsync(id, User.GetUserId(), bookId, cancellationToken), "Book shared.");

    [HttpPost]
    public Task<IActionResult> Unshare(Guid id, Guid contentId, CancellationToken cancellationToken)
        => RunAndReturnAsync(id, Tab, () => _library.UnshareContentAsync(id, User.GetUserId(), contentId, cancellationToken), "Removed from classroom.");

    [HttpPost]
    public async Task<IActionResult> CopyQuiz(Guid id, Guid quizId, CancellationToken cancellationToken)
    {
        var copy = await _quizzes.CopyClassroomQuizAsync(quizId, id, User.GetUserId(), cancellationToken);
        if (copy == null)
        {
            TempData[NotificationKeys.Classroom] = "That quiz could not be copied.";
            return BackToClassroom(id, Tab);
        }

        TempData[NotificationKeys.Quiz] = $"Copied {copy.Name} to your library.";
        return RedirectToAction("Details", "Quiz", new { id = copy.Id });
    }

    [HttpGet]
    public async Task<IActionResult> ContentPdf(Guid id, Guid bookId, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        try
        {
            var book = await _library.RequireSharedBookAsync(id, bookId, userId, cancellationToken);
            var stream = await _books.OpenPdfUncheckedAsync(book.Id, cancellationToken);
            return File(stream, "application/pdf", enableRangeProcessing: true);
        }
        catch (ClassroomAccessDeniedException)
        {
            return NotFound();
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
    }
}
