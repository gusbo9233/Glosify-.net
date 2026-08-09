using Glosify.Data;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Classrooms;

/// <summary>
/// Lessons and the assignments hung off them, plus the schedule view that combines the two.
/// </summary>
public interface IClassroomPlanner
{
    Task<ClassroomLesson> CreateLessonAsync(Guid classroomId, string userId, string title, string? description, DateTimeOffset? scheduledAt, CancellationToken cancellationToken = default);
    Task DeleteLessonAsync(Guid classroomId, string userId, Guid lessonId, CancellationToken cancellationToken = default);
    Task<ClassroomAssignment> CreateAssignmentAsync(Guid classroomId, string userId, string title, string? instructions, Guid? quizId, Guid? lessonId, DateTimeOffset? dueAt, CancellationToken cancellationToken = default);
    Task DeleteAssignmentAsync(Guid classroomId, string userId, Guid assignmentId, CancellationToken cancellationToken = default);
    Task<ClassroomSchedule> GetScheduleAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);
}

public sealed class ClassroomPlanner : IClassroomPlanner
{
    private readonly GlosifyContext _context;
    private readonly IClassroomAccess _access;
    private readonly ClassroomQueries _queries;

    public ClassroomPlanner(
        GlosifyContext context,
        IClassroomAccess access,
        ClassroomQueries queries)
    {
        _context = context;
        _access = access;
        _queries = queries;
    }

    public async Task<ClassroomLesson> CreateLessonAsync(Guid classroomId, string userId, string title, string? description, DateTimeOffset? scheduledAt, CancellationToken cancellationToken = default)
    {
        await _access.RequireTeacherAsync(classroomId, userId, cancellationToken);

        title = title.Trim();
        if (title.Length == 0)
        {
            throw new ArgumentException("Give the lesson a title.");
        }

        var lesson = new ClassroomLesson
        {
            Id = Guid.NewGuid(),
            ClassroomId = classroomId,
            Title = title,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            ScheduledAt = scheduledAt,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _context.ClassroomLessons.Add(lesson);
        await _context.SaveChangesAsync(cancellationToken);
        return lesson;
    }

    public async Task DeleteLessonAsync(Guid classroomId, string userId, Guid lessonId, CancellationToken cancellationToken = default)
    {
        await _access.RequireTeacherAsync(classroomId, userId, cancellationToken);

        var lesson = await _context.ClassroomLessons
            .FirstOrDefaultAsync(l => l.Id == lessonId && l.ClassroomId == classroomId, cancellationToken);
        if (lesson == null)
        {
            return;
        }

        // LessonId is a NoAction FK, so detach assignments before the lesson goes;
        // they survive as unattached assignments.
        var assignments = await _context.ClassroomAssignments
            .Where(a => a.LessonId == lessonId)
            .ToListAsync(cancellationToken);
        foreach (var assignment in assignments)
        {
            assignment.LessonId = null;
        }

        _context.ClassroomLessons.Remove(lesson);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<ClassroomAssignment> CreateAssignmentAsync(Guid classroomId, string userId, string title, string? instructions, Guid? quizId, Guid? lessonId, DateTimeOffset? dueAt, CancellationToken cancellationToken = default)
    {
        await _access.RequireTeacherAsync(classroomId, userId, cancellationToken);

        title = title.Trim();
        if (title.Length == 0)
        {
            throw new ArgumentException("Give the assignment a title.");
        }

        if (quizId.HasValue)
        {
            var quizShared = await _context.ClassroomContents
                .AnyAsync(c => c.ClassroomId == classroomId && c.QuizId == quizId.Value, cancellationToken);
            if (!quizShared)
            {
                throw new ArgumentException("Share the quiz with the classroom before assigning it.");
            }
        }

        if (lessonId.HasValue)
        {
            var lessonExists = await _context.ClassroomLessons
                .AnyAsync(l => l.Id == lessonId.Value && l.ClassroomId == classroomId, cancellationToken);
            if (!lessonExists)
            {
                throw new ArgumentException("That lesson does not belong to this classroom.");
            }
        }

        var assignment = new ClassroomAssignment
        {
            Id = Guid.NewGuid(),
            ClassroomId = classroomId,
            LessonId = lessonId,
            Title = title,
            Instructions = string.IsNullOrWhiteSpace(instructions) ? null : instructions.Trim(),
            QuizId = quizId,
            DueAt = dueAt,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _context.ClassroomAssignments.Add(assignment);
        await _context.SaveChangesAsync(cancellationToken);
        return assignment;
    }

    public async Task DeleteAssignmentAsync(Guid classroomId, string userId, Guid assignmentId, CancellationToken cancellationToken = default)
    {
        await _access.RequireTeacherAsync(classroomId, userId, cancellationToken);

        var assignment = await _context.ClassroomAssignments
            .FirstOrDefaultAsync(a => a.Id == assignmentId && a.ClassroomId == classroomId, cancellationToken);
        if (assignment != null)
        {
            _context.ClassroomAssignments.Remove(assignment);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<ClassroomSchedule> GetScheduleAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default)
    {
        await _access.RequireMemberAsync(classroomId, userId, cancellationToken);
        return await _queries.ScheduleAsync(classroomId, userId, cancellationToken);
    }
}
