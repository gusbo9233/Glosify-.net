using Glosify.Extensions;
using Glosify.Models;
using Glosify.Models.ViewModels;
using Glosify.Services.Classrooms;
using Glosify.Services.Communication;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers.Classrooms;

/// <summary>
/// The classroom's video call page and the ACS token that joins it.
/// </summary>
public sealed class ClassroomCallController : ClassroomControllerBase
{
    private readonly IClassroomCall _call;
    private readonly IAcsTokenService _acsTokens;
    private readonly ILogger<ClassroomCallController> _logger;

    public ClassroomCallController(
        IClassroomCall call,
        IAcsTokenService acsTokens,
        ILogger<ClassroomCallController> logger)
        : base(logger)
    {
        _call = call;
        _acsTokens = acsTokens;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Call(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var entry = await _call.GetEntryAsync(id, User.GetUserId(), cancellationToken);
            ViewData["HideAssistantPanel"] = true;
            return View(new ClassroomCallViewModel
            {
                Classroom = entry.Classroom,
                IsCallingConfigured = _acsTokens.IsConfigured,
                DisplayName = User.Identity?.Name ?? "Member",
                IsTeacher = entry.IsTeacher,
                ActiveCallParticipants = entry.ActiveParticipants
            });
        }
        catch (ClassroomAccessDeniedException)
        {
            TempData[NotificationKeys.Classroom] = "Classroom not found.";
            return BackToClassroomList();
        }
    }

    [HttpPost]
    public async Task<IActionResult> CallToken(Guid id, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        try
        {
            var entry = await _call.RequireJoinableAsync(id, userId, cancellationToken);
            var token = await _acsTokens.GetCallTokenAsync(userId, cancellationToken);
            return Json(new
            {
                token = token.Token,
                expiresOn = token.ExpiresOn,
                acsUserId = token.AcsUserId,
                groupCallId = entry.Classroom.GroupCallId
            });
        }
        catch (ClassroomCallNotStartedException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
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
}
