using Glosify.Services.Classrooms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers.Classrooms;

/// <summary>
/// Shared behaviour for the controllers behind the /Classroom URL space.
/// </summary>
/// <remarks>
/// The classroom surface is one page with tabs, so almost every POST does its work and then
/// returns to that page on the right tab. <see cref="RunAndReturnAsync"/> is that shape.
/// <para>
/// This base carries <c>[Route("Classroom/[action]/{id?}")]</c>, which is the shape the
/// default conventional route already gave these actions, so every URL stays exactly where
/// it was before the split — including the id-in-the-path form that RedirectToAction
/// generates. The URL space is the app's contract with browsers, bookmarks and the two
/// hard-coded fetches in the classroom JavaScript; how the actions are grouped into
/// classes is not.
/// </para>
/// </remarks>
[Authorize]
[Route("Classroom/[action]/{id?}")]
public abstract class ClassroomControllerBase : Controller
{
    private readonly ILogger _logger;

    protected ClassroomControllerBase(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Redirects to the classroom details page, on <paramref name="tab"/> when given.
    /// </summary>
    protected IActionResult BackToClassroom(Guid classroomId, string? tab = null) =>
        tab is null
            ? RedirectToAction("Details", "Classroom", new { id = classroomId })
            : RedirectToAction("Details", "Classroom", new { id = classroomId, tab });

    protected IActionResult BackToClassroomList() => RedirectToAction("Index", "Classroom");

    protected async Task<IActionResult> RunAndReturnAsync(
        Guid classroomId,
        string tab,
        Func<Task> action,
        string? successMessage = null)
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

        return BackToClassroom(classroomId, tab);
    }
}
