using Glosify.Extensions;
using Glosify.Services.Classrooms;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers.Classrooms;

/// <summary>
/// The planning tab: lessons and the assignments attached to them.
/// </summary>
public sealed class ClassroomPlanningController : ClassroomControllerBase
{
    private const string Tab = "planning";

    private readonly IClassroomPlanner _planner;

    public ClassroomPlanningController(
        IClassroomPlanner planner,
        ILogger<ClassroomPlanningController> logger)
        : base(logger)
    {
        _planner = planner;
    }

    [HttpPost]
    public Task<IActionResult> CreateLesson(Guid id, string title, string? description, DateTimeOffset? scheduledAt, CancellationToken cancellationToken)
        => RunAndReturnAsync(id, Tab,
            () => _planner.CreateLessonAsync(id, User.GetUserId(), title ?? string.Empty, description, scheduledAt, cancellationToken),
            "Lesson created.");

    [HttpPost]
    public Task<IActionResult> DeleteLesson(Guid id, Guid lessonId, CancellationToken cancellationToken)
        => RunAndReturnAsync(id, Tab, () => _planner.DeleteLessonAsync(id, User.GetUserId(), lessonId, cancellationToken));

    [HttpPost]
    public Task<IActionResult> CreateAssignment(Guid id, string title, string? instructions, Guid? quizId, Guid? lessonId, DateTimeOffset? dueAt, CancellationToken cancellationToken)
        => RunAndReturnAsync(id, Tab,
            () => _planner.CreateAssignmentAsync(id, User.GetUserId(), title ?? string.Empty, instructions, quizId, lessonId, dueAt, cancellationToken),
            "Assignment created.");

    [HttpPost]
    public Task<IActionResult> DeleteAssignment(Guid id, Guid assignmentId, CancellationToken cancellationToken)
        => RunAndReturnAsync(id, Tab, () => _planner.DeleteAssignmentAsync(id, User.GetUserId(), assignmentId, cancellationToken));
}
