using Glosify.Services.Books;
using Glosify.Services.Classrooms;
using Glosify.Services.Communication;
using Glosify.Services.Quizzes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[Authorize]
public class ClassroomController : Controller
{
    private readonly IClassroomService _classrooms;
    private readonly IQuizService _quizzes;
    private readonly IBookDocumentService _books;
    private readonly IAcsTokenService _acsTokens;
    private readonly ILogger<ClassroomController> _logger;

    public ClassroomController(
        IClassroomService classrooms,
        IQuizService quizzes,
        IBookDocumentService books,
        IAcsTokenService acsTokens,
        ILogger<ClassroomController> logger)
    {
        _classrooms = classrooms;
        _quizzes = quizzes;
        _books = books;
        _acsTokens = acsTokens;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        return View(new ClassroomIndexViewModel
        {
            Classrooms = await _classrooms.GetForUserAsync(userId, cancellationToken),
            PendingInvitations = await _classrooms.GetPendingInvitationsForUserAsync(userId, cancellationToken)
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create(string name, string? description, CancellationToken cancellationToken)
    {
        try
        {
            var classroom = await _classrooms.CreateAsync(User.GetUserId(), name ?? string.Empty, description, cancellationToken);
            TempData[NotificationKeys.Classroom] = $"Created {classroom.Name}.";
            return RedirectToAction(nameof(Details), new { id = classroom.Id });
        }
        catch (ArgumentException ex)
        {
            TempData[NotificationKeys.Classroom] = ex.Message;
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost]
    public async Task<IActionResult> Join(string code, CancellationToken cancellationToken)
    {
        var classroom = await _classrooms.JoinByCodeAsync(User.GetUserId(), code ?? string.Empty, cancellationToken);
        if (classroom == null)
        {
            TempData[NotificationKeys.Classroom] = "No classroom matches that join code.";
            return RedirectToAction(nameof(Index));
        }

        TempData[NotificationKeys.Classroom] = $"Welcome to {classroom.Name}.";
        return RedirectToAction(nameof(Details), new { id = classroom.Id });
    }

    [HttpPost]
    public async Task<IActionResult> AcceptInvitation(Guid id, CancellationToken cancellationToken)
    {
        var classroom = await _classrooms.AcceptInvitationAsync(id, User.GetUserId(), cancellationToken);
        if (classroom == null)
        {
            TempData[NotificationKeys.Classroom] = "That invitation is no longer available.";
            return RedirectToAction(nameof(Index));
        }

        TempData[NotificationKeys.Classroom] = $"Welcome to {classroom.Name}.";
        return RedirectToAction(nameof(Details), new { id = classroom.Id });
    }

    [HttpPost]
    public async Task<IActionResult> DeclineInvitation(Guid id, CancellationToken cancellationToken)
    {
        await _classrooms.DeclineInvitationAsync(id, User.GetUserId(), cancellationToken);
        TempData[NotificationKeys.Classroom] = "Invitation declined.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Details(Guid id, string? tab, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();

        try
        {
            var membership = await _classrooms.RequireMemberAsync(id, userId, cancellationToken);
            var classroom = await _classrooms.GetDetailsAsync(id, userId, cancellationToken);
            var isTeacher = membership.Role is ClassroomRole.Owner or ClassroomRole.Teacher;

            var activeTab = (tab ?? "stream").ToLowerInvariant();
            if (activeTab == "results" && !isTeacher)
            {
                activeTab = "stream";
            }

            var model = new ClassroomDetailsViewModel
            {
                Classroom = classroom,
                CurrentRole = membership.Role,
                ActiveTab = activeTab,
                Board = await _classrooms.GetBoardAsync(id, userId, cancellationToken),
                Members = await _classrooms.GetMembersAsync(id, userId, cancellationToken),
                Content = await _classrooms.GetContentAsync(id, userId, cancellationToken),
                UnreadChatCount = await _classrooms.GetUnreadChatCountAsync(id, userId, cancellationToken)
            };

            if (isTeacher)
            {
                var sharedQuizIds = model.Content.Where(c => c.Quiz != null).Select(c => c.Quiz!.Id).ToHashSet();
                var sharedBookIds = model.Content.Where(c => c.Book != null).Select(c => c.Book!.Id).ToHashSet();
                model.ShareableQuizzes = (await _quizzes.GetUserQuizzesAsync(userId, cancellationToken))
                    .Where(q => !sharedQuizIds.Contains(q.Id))
                    .OrderBy(q => q.Name)
                    .ToList();
                model.ShareableBooks = (await _books.GetUserBooksAsync(userId, cancellationToken))
                    .Where(b => !sharedBookIds.Contains(b.Id))
                    .ToList();
                model.Results = await _classrooms.GetClassroomResultsAsync(id, userId, cancellationToken);
            }

            return View(model);
        }
        catch (ClassroomAccessDeniedException)
        {
            TempData[NotificationKeys.Classroom] = "Classroom not found.";
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpGet]
    public async Task<IActionResult> ChatHistory(Guid id, DateTimeOffset? before, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        try
        {
            var messages = await _classrooms.GetChatMessagesAsync(id, userId, before, take: 50, cancellationToken);
            if (!before.HasValue)
            {
                await _classrooms.MarkChatReadAsync(id, userId, cancellationToken);
            }

            return Json(new
            {
                currentUserId = userId,
                messages = messages.Select(m => new
                {
                    id = m.Id,
                    userId = m.UserId,
                    authorName = m.AuthorName,
                    body = m.Body,
                    createdAt = m.CreatedAt
                })
            });
        }
        catch (ClassroomAccessDeniedException)
        {
            return NotFound();
        }
    }

    [HttpPost]
    public Task<IActionResult> PostAnnouncement(Guid id, string body, CancellationToken cancellationToken)
        => RunAndReturnAsync(id, "stream", () => _classrooms.PostAnnouncementAsync(id, User.GetUserId(), body ?? string.Empty, cancellationToken));

    [HttpPost]
    public Task<IActionResult> DeleteMessage(Guid id, Guid messageId, CancellationToken cancellationToken)
        => RunAndReturnAsync(id, "stream", () => _classrooms.DeleteMessageAsync(id, User.GetUserId(), messageId, cancellationToken));

    [HttpPost]
    public Task<IActionResult> Pin(Guid id, Guid messageId, bool pinned, CancellationToken cancellationToken)
        => RunAndReturnAsync(id, "stream", () => _classrooms.SetPinnedAsync(id, User.GetUserId(), messageId, pinned, cancellationToken));

    [HttpPost]
    public Task<IActionResult> Invite(Guid id, string email, string? role, CancellationToken cancellationToken)
    {
        var invitedRole = string.Equals(role, "teacher", StringComparison.OrdinalIgnoreCase)
            ? ClassroomRole.Teacher
            : ClassroomRole.Student;
        return RunAndReturnAsync(id, "members",
            () => _classrooms.InviteByEmailAsync(id, User.GetUserId(), email ?? string.Empty, invitedRole, cancellationToken),
            "Invitation created. It appears on their Classroom page next time they visit.");
    }

    [HttpPost]
    public Task<IActionResult> RemoveMember(Guid id, string memberUserId, CancellationToken cancellationToken)
        => RunAndReturnAsync(id, "members", () => _classrooms.RemoveMemberAsync(id, User.GetUserId(), memberUserId, cancellationToken));

    [HttpPost]
    public Task<IActionResult> ChangeRole(Guid id, string memberUserId, string role, CancellationToken cancellationToken)
    {
        var newRole = string.Equals(role, "teacher", StringComparison.OrdinalIgnoreCase)
            ? ClassroomRole.Teacher
            : ClassroomRole.Student;
        return RunAndReturnAsync(id, "members", () => _classrooms.ChangeRoleAsync(id, User.GetUserId(), memberUserId, newRole, cancellationToken));
    }

    [HttpPost]
    public async Task<IActionResult> Leave(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await _classrooms.LeaveAsync(id, User.GetUserId(), cancellationToken);
            TempData[NotificationKeys.Classroom] = "You left the classroom.";
        }
        catch (ClassroomAccessDeniedException ex)
        {
            TempData[NotificationKeys.Classroom] = ex.Message;
            return RedirectToAction(nameof(Details), new { id });
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await _classrooms.DeleteClassroomAsync(id, User.GetUserId(), cancellationToken);
            TempData[NotificationKeys.Classroom] = "Classroom deleted.";
        }
        catch (ClassroomAccessDeniedException ex)
        {
            TempData[NotificationKeys.Classroom] = ex.Message;
            return RedirectToAction(nameof(Details), new { id });
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public Task<IActionResult> RegenerateJoinCode(Guid id, CancellationToken cancellationToken)
        => RunAndReturnAsync(id, "members", () => _classrooms.RegenerateJoinCodeAsync(id, User.GetUserId(), cancellationToken), "Join code regenerated.");

    [HttpPost]
    public Task<IActionResult> ToggleJoinCode(Guid id, bool enabled, CancellationToken cancellationToken)
        => RunAndReturnAsync(id, "members", () => _classrooms.SetJoinCodeEnabledAsync(id, User.GetUserId(), enabled, cancellationToken));

    [HttpPost]
    public Task<IActionResult> ShareQuiz(Guid id, Guid quizId, CancellationToken cancellationToken)
        => RunAndReturnAsync(id, "content", () => _classrooms.ShareQuizAsync(id, User.GetUserId(), quizId, cancellationToken), "Quiz shared.");

    [HttpPost]
    public Task<IActionResult> ShareBook(Guid id, Guid bookId, CancellationToken cancellationToken)
        => RunAndReturnAsync(id, "content", () => _classrooms.ShareBookAsync(id, User.GetUserId(), bookId, cancellationToken), "Book shared.");

    [HttpPost]
    public Task<IActionResult> Unshare(Guid id, Guid contentId, CancellationToken cancellationToken)
        => RunAndReturnAsync(id, "content", () => _classrooms.UnshareContentAsync(id, User.GetUserId(), contentId, cancellationToken), "Removed from classroom.");

    [HttpPost]
    public async Task<IActionResult> CopyQuiz(Guid id, Guid quizId, CancellationToken cancellationToken)
    {
        var copy = await _quizzes.CopyClassroomQuizAsync(quizId, id, User.GetUserId(), cancellationToken);
        if (copy == null)
        {
            TempData[NotificationKeys.Classroom] = "That quiz could not be copied.";
            return RedirectToAction(nameof(Details), new { id, tab = "content" });
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
            var book = await _classrooms.RequireSharedBookAsync(id, bookId, userId, cancellationToken);
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

    [HttpGet]
    public async Task<IActionResult> Call(Guid id, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        try
        {
            var classroom = await _classrooms.GetDetailsAsync(id, userId, cancellationToken);
            ViewData["HideAssistantPanel"] = true;
            return View(new ClassroomCallViewModel
            {
                Classroom = classroom,
                IsCallingConfigured = _acsTokens.IsConfigured,
                DisplayName = User.Identity?.Name ?? "Member"
            });
        }
        catch (ClassroomAccessDeniedException)
        {
            TempData[NotificationKeys.Classroom] = "Classroom not found.";
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost]
    public async Task<IActionResult> CallToken(Guid id, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        try
        {
            var classroom = await _classrooms.GetDetailsAsync(id, userId, cancellationToken);
            var token = await _acsTokens.GetCallTokenAsync(userId, cancellationToken);
            return Json(new
            {
                token = token.Token,
                expiresOn = token.ExpiresOn,
                acsUserId = token.AcsUserId,
                groupCallId = classroom.GroupCallId
            });
        }
        catch (ClassroomAccessDeniedException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ACS token issuance failed for classroom {ClassroomId}", id);
            return StatusCode(StatusCodes.Status502BadGateway, new { message = "Could not start the call. Try again." });
        }
    }

    [HttpGet]
    public async Task<IActionResult> MemberResults(Guid id, string memberId, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        try
        {
            var classroom = await _classrooms.GetDetailsAsync(id, userId, cancellationToken);
            var results = await _classrooms.GetMemberResultsAsync(id, userId, memberId, cancellationToken);
            var members = await _classrooms.GetMembersAsync(id, userId, cancellationToken);
            var member = members.FirstOrDefault(m => m.UserId == memberId);

            return View(new ClassroomMemberResultsViewModel
            {
                Classroom = classroom,
                MemberUserId = memberId,
                MemberName = member?.DisplayName ?? "Unknown",
                Results = results
            });
        }
        catch (ClassroomAccessDeniedException)
        {
            TempData[NotificationKeys.Classroom] = "You do not have access to those results.";
            return RedirectToAction(nameof(Details), new { id });
        }
    }

    private async Task<IActionResult> RunAndReturnAsync(Guid classroomId, string tab, Func<Task> action, string? successMessage = null)
    {
        try
        {
            await action();
            if (successMessage != null)
            {
                TempData[NotificationKeys.Classroom] = successMessage;
            }
        }
        catch (Exception ex) when (ex is ClassroomAccessDeniedException or ArgumentException)
        {
            TempData[NotificationKeys.Classroom] = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Classroom action failed for classroom {ClassroomId}", classroomId);
            TempData[NotificationKeys.Classroom] = "Something went wrong. Try again.";
        }

        return RedirectToAction(nameof(Details), new { id = classroomId, tab });
    }
}
