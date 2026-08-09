using Glosify.Hubs;
using Glosify.Services.Classrooms;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Glosify.Controllers.Classrooms;

/// <summary>
/// The members tab: invitations, removals, role changes, the join code, and one member's
/// results.
/// </summary>
public sealed class ClassroomMembersController : ClassroomControllerBase
{
    private const string Tab = "members";

    private readonly IClassroomDirectory _classrooms;
    private readonly IClassroomRoster _roster;
    private readonly IClassroomResults _results;
    private readonly IHubContext<ClassroomChatHub> _chatHub;

    public ClassroomMembersController(
        IClassroomDirectory classrooms,
        IClassroomRoster roster,
        IClassroomResults results,
        IHubContext<ClassroomChatHub> chatHub,
        ILogger<ClassroomMembersController> logger)
        : base(logger)
    {
        _classrooms = classrooms;
        _roster = roster;
        _results = results;
        _chatHub = chatHub;
    }

    [HttpPost]
    public Task<IActionResult> Invite(Guid id, string email, string? role, CancellationToken cancellationToken)
    {
        var invitedRole = string.Equals(role, "teacher", StringComparison.OrdinalIgnoreCase)
            ? ClassroomRole.Teacher
            : ClassroomRole.Student;
        return RunAndReturnAsync(id, Tab,
            () => _roster.InviteByEmailAsync(id, User.GetUserId(), email ?? string.Empty, invitedRole, cancellationToken),
            "Invitation created. It appears on their Classroom page next time they visit.");
    }

    [HttpPost]
    public Task<IActionResult> RemoveMember(Guid id, string memberUserId, CancellationToken cancellationToken)
        => RunAndReturnAsync(id, Tab, async () =>
        {
            await _roster.RemoveMemberAsync(id, User.GetUserId(), memberUserId, cancellationToken);
            await ClassroomChatHub.EvictFromGroupAsync(_chatHub, id, memberUserId, cancellationToken);
        });

    [HttpPost]
    public Task<IActionResult> ChangeRole(Guid id, string memberUserId, string role, CancellationToken cancellationToken)
    {
        var newRole = string.Equals(role, "teacher", StringComparison.OrdinalIgnoreCase)
            ? ClassroomRole.Teacher
            : ClassroomRole.Student;
        return RunAndReturnAsync(id, Tab, () => _roster.ChangeRoleAsync(id, User.GetUserId(), memberUserId, newRole, cancellationToken));
    }

    [HttpPost]
    public Task<IActionResult> RegenerateJoinCode(Guid id, CancellationToken cancellationToken)
        => RunAndReturnAsync(id, Tab, () => _classrooms.RegenerateJoinCodeAsync(id, User.GetUserId(), cancellationToken), "Join code regenerated.");

    [HttpPost]
    public Task<IActionResult> ToggleJoinCode(Guid id, bool enabled, CancellationToken cancellationToken)
        => RunAndReturnAsync(id, Tab, () => _classrooms.SetJoinCodeEnabledAsync(id, User.GetUserId(), enabled, cancellationToken));

    [HttpGet]
    public async Task<IActionResult> MemberResults(Guid id, string memberId, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        try
        {
            var classroom = await _classrooms.GetDetailsAsync(id, userId, cancellationToken);
            var results = await _results.GetMemberResultsAsync(id, userId, memberId, cancellationToken);
            var memberName = await _roster.GetMemberNameAsync(id, userId, memberId, cancellationToken);

            return View(new ClassroomMemberResultsViewModel
            {
                Classroom = classroom,
                MemberUserId = memberId,
                MemberName = memberName ?? "Unknown",
                Results = results
            });
        }
        catch (ClassroomAccessDeniedException)
        {
            TempData[NotificationKeys.Classroom] = "You do not have access to those results.";
            return BackToClassroom(id);
        }
    }
}
