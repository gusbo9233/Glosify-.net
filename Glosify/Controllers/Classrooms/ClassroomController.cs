using Glosify.Extensions;
using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Models.ViewModels;
using Glosify.Services.Books;
using Glosify.Services.Classrooms;
using Glosify.Services.CustomQuizzes;
using Glosify.Services.Language;
using Glosify.Services.Quizzes;
using Glosify.Localization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers.Classrooms;

/// <summary>
/// The classroom list and the classroom page itself: joining, creating, opening, leaving
/// and deleting. Everything a member does <em>inside</em> a classroom lives on one of the
/// sibling controllers.
/// </summary>
public sealed class ClassroomController : ClassroomControllerBase
{
    private readonly IClassroomDirectory _classrooms;
    private readonly IClassroomRoster _roster;
    private readonly IClassroomResults _results;
    private readonly IQuizService _quizzes;
    private readonly IBookDocumentService _books;
    private readonly ICustomQuizService _customQuizzes;
    private readonly IClassroomCallPresence _callPresence;
    private readonly ILanguageContext _languageContext;

    public ClassroomController(
        IClassroomDirectory classrooms,
        IClassroomRoster roster,
        IClassroomResults results,
        IQuizService quizzes,
        IBookDocumentService books,
        ICustomQuizService customQuizzes,
        IClassroomCallPresence callPresence,
        ILanguageContext languageContext,
        ILogger<ClassroomController> logger)
        : base(logger)
    {
        _classrooms = classrooms;
        _roster = roster;
        _results = results;
        _quizzes = quizzes;
        _books = books;
        _customQuizzes = customQuizzes;
        _callPresence = callPresence;
        _languageContext = languageContext;
    }

    // /Classroom on its own used to reach Index through the conventional route's
    // action default; attribute routing needs it spelled out.
    [HttpGet("/Classroom")]
    [HttpGet("/Classroom/Index")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        return View(new ClassroomIndexViewModel
        {
            Classrooms = await _classrooms.GetForUserAsync(userId, _languageContext.CurrentLanguage, cancellationToken),
            PendingInvitations = await _roster.GetPendingInvitationsForUserAsync(userId, cancellationToken)
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create(string name, string? description, CancellationToken cancellationToken)
    {
        try
        {
            var classroom = await _classrooms.CreateAsync(
                User.GetUserId(),
                name ?? string.Empty,
                description,
                _languageContext.CurrentLanguage,
                cancellationToken);
            TempData[NotificationKeys.Classroom] = Text["Classroom.Created", classroom.Name].Value;
            return BackToClassroom(classroom.Id);
        }
        catch (ArgumentException)
        {
            TempData[NotificationKeys.Classroom] = Text["Classroom.CreateInvalid"].Value;
            return BackToClassroomList();
        }
    }

    [HttpPost]
    public async Task<IActionResult> Join(string code, CancellationToken cancellationToken)
    {
        var classroom = await _classrooms.JoinByCodeAsync(User.GetUserId(), code ?? string.Empty, cancellationToken);
        if (classroom == null)
        {
            TempData[NotificationKeys.Classroom] = Text["Classroom.NoCodeMatch"].Value;
            return BackToClassroomList();
        }

        TempData[NotificationKeys.Classroom] = WelcomeMessage(classroom);
        return BackToClassroom(classroom.Id);
    }

    [HttpPost]
    public async Task<IActionResult> AcceptInvitation(Guid id, CancellationToken cancellationToken)
    {
        var classroom = await _roster.AcceptInvitationAsync(id, User.GetUserId(), cancellationToken);
        if (classroom == null)
        {
            TempData[NotificationKeys.Classroom] = Text["Classroom.InvitationUnavailable"].Value;
            return BackToClassroomList();
        }

        TempData[NotificationKeys.Classroom] = WelcomeMessage(classroom);
        return BackToClassroom(classroom.Id);
    }

    /// <summary>
    /// Follows the classroom into its language, so a classroom someone just joined
    /// is not immediately filtered out of their list.
    /// </summary>
    private string WelcomeMessage(ClassroomHeader classroom)
    {
        if (string.IsNullOrWhiteSpace(classroom.Language)
            || string.Equals(classroom.Language, _languageContext.CurrentLanguage, StringComparison.Ordinal)
            || !_languageContext.TrySetLanguage(classroom.Language))
        {
            return Text["Classroom.Welcome", classroom.Name];
        }

        return Text["Classroom.WelcomeSwitched", classroom.Name, QuizLanguageDisplay.Name(classroom.Language)];
    }

    [HttpPost]
    public async Task<IActionResult> DeclineInvitation(Guid id, CancellationToken cancellationToken)
    {
        await _roster.DeclineInvitationAsync(id, User.GetUserId(), cancellationToken);
        TempData[NotificationKeys.Classroom] = Text["Classroom.InvitationDeclined"].Value;
        return BackToClassroomList();
    }

    [HttpGet]
    public async Task<IActionResult> Details(Guid id, string? tab, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();

        try
        {
            var page = await _classrooms.GetDetailsPageAsync(id, userId, cancellationToken);
            var isTeacher = page.Role is ClassroomRole.Owner or ClassroomRole.Teacher;

            var activeTab = (tab ?? "stream").ToLowerInvariant();
            if (activeTab == "results" && !isTeacher)
            {
                activeTab = "stream";
            }

            var model = new ClassroomDetailsViewModel
            {
                Classroom = page.Classroom,
                CurrentRole = page.Role,
                ActiveTab = activeTab,
                ActiveCallParticipants = _callPresence.GetParticipantCount(id),
                Board = page.Board,
                Members = page.Members,
                Content = page.Content,
                UnreadChatCount = page.UnreadChatCount,
                Schedule = page.Schedule
            };

            var customQuizSourceIds = activeTab switch
            {
                "content" => page.Content
                    .Where(item => item.Quiz is not null)
                    .Select(item => item.Quiz!.Id),
                "planning" => page.Schedule.Lessons
                    .SelectMany(lesson => lesson.Assignments)
                    .Concat(page.Schedule.UnattachedAssignments)
                    .Where(info => info.Assignment.QuizId.HasValue)
                    .Select(info => info.Assignment.QuizId!.Value),
                _ => []
            };
            model.CustomQuizzesByQuizId = await _customQuizzes.ListForQuizzesAsync(
                customQuizSourceIds.Distinct().ToList(),
                playableOnly: true,
                cancellationToken);

            if (isTeacher)
            {
                var sharedQuizIds = model.Content.Where(c => c.Quiz != null).Select(c => c.Quiz!.Id).ToHashSet();
                var sharedBookIds = model.Content.Where(c => c.Book != null).Select(c => c.Book!.Id).ToHashSet();
                model.ShareableQuizzes = (await _quizzes.GetUserQuizzesAsync(userId, cancellationToken))
                    .Where(q => !sharedQuizIds.Contains(q.Id))
                    .OrderBy(q => q.Name)
                    .Select(ClassroomQuizRef.From)
                    .ToList();
                model.ShareableBooks = (await _books.GetUserBooksAsync(userId, cancellationToken))
                    .Where(b => !sharedBookIds.Contains(b.Id))
                    .Select(ClassroomBookRef.From)
                    .ToList();
                model.Results = await _results.GetClassroomResultsAsync(id, userId, cancellationToken);
            }

            return View(model);
        }
        catch (ClassroomAccessDeniedException)
        {
            TempData[NotificationKeys.Classroom] = Text["Classroom.NotFound"].Value;
            return BackToClassroomList();
        }
    }

    [HttpPost]
    public async Task<IActionResult> Leave(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await _roster.LeaveAsync(id, User.GetUserId(), cancellationToken);
            TempData[NotificationKeys.Classroom] = Text["Classroom.Left"].Value;
        }
        catch (ClassroomAccessDeniedException)
        {
            TempData[NotificationKeys.Classroom] = Text["Classroom.ActionDenied"].Value;
            return BackToClassroom(id);
        }

        return BackToClassroomList();
    }

    [HttpPost]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await _classrooms.DeleteClassroomAsync(id, User.GetUserId(), cancellationToken);
            TempData[NotificationKeys.Classroom] = Text["Classroom.Deleted"].Value;
        }
        catch (ClassroomAccessDeniedException)
        {
            TempData[NotificationKeys.Classroom] = Text["Classroom.ActionDenied"].Value;
            return BackToClassroom(id);
        }

        return BackToClassroomList();
    }
}
