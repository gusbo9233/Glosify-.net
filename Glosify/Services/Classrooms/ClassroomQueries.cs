using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Models.Library;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Classrooms;

/// <summary>
/// The classroom read queries, with no authorization of their own.
/// </summary>
/// <remarks>
/// Every caller is a classroom service that has already established the caller's role, so
/// these deliberately do not check again. That separation is what lets
/// <see cref="ClassroomDirectory.GetDetailsPageAsync"/> assemble the whole details page
/// behind a single membership check instead of one per section.
/// </remarks>
public sealed class ClassroomQueries
{
    private readonly GlosifyContext _context;

    public ClassroomQueries(GlosifyContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<ClassroomMemberInfo>> MembersAsync(
        Guid classroomId,
        CancellationToken cancellationToken)
    {
        return await _context.ClassroomMemberships
            .AsNoTracking()
            .Where(m => m.ClassroomId == classroomId)
            .Join(_context.Users.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => new { m, u })
            .OrderBy(x => x.m.Role)
            .ThenBy(x => x.m.JoinedAt)
            .Select(x => new ClassroomMemberInfo(
                x.u.Id,
                x.u.UserName ?? x.u.Email ?? "Unknown",
                x.u.Email ?? string.Empty,
                x.m.Role,
                x.m.JoinedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ClassroomContentItem>> ContentAsync(
        Guid classroomId,
        CancellationToken cancellationToken)
    {
        var links = await _context.ClassroomContents
            .AsNoTracking()
            .Where(c => c.ClassroomId == classroomId)
            .Join(_context.Users.AsNoTracking(), c => c.SharedByUserId, u => u.Id, (c, u) => new { Link = c, SharedByName = u.UserName ?? u.Email ?? "Unknown" })
            .OrderByDescending(x => x.Link.SharedAt)
            .ToListAsync(cancellationToken);

        var quizIds = links.Where(x => x.Link.QuizId.HasValue).Select(x => x.Link.QuizId!.Value).ToList();
        var bookIds = links.Where(x => x.Link.BookDocumentId.HasValue).Select(x => x.Link.BookDocumentId!.Value).ToList();

        var quizzes = quizIds.Count == 0
            ? new Dictionary<Guid, ClassroomQuizRef>()
            : await _context.Quizzes.AsNoTracking()
                .Where(q => quizIds.Contains(q.Id))
                .Select(q => new ClassroomQuizRef(q.Id, q.Name, q.TargetLanguage))
                .ToDictionaryAsync(q => q.Id, cancellationToken);
        var books = bookIds.Count == 0
            ? new Dictionary<Guid, ClassroomBookRef>()
            : await _context.BookDocuments.AsNoTracking()
                .Where(b => bookIds.Contains(b.Id))
                .Select(b => new ClassroomBookRef(b.Id, b.Title))
                .ToDictionaryAsync(b => b.Id, cancellationToken);

        return links
            .Select(x => new ClassroomContentItem(
                x.Link.Id,
                x.Link.ContentType,
                x.Link.QuizId.HasValue ? quizzes.GetValueOrDefault(x.Link.QuizId.Value) : null,
                x.Link.BookDocumentId.HasValue ? books.GetValueOrDefault(x.Link.BookDocumentId.Value) : null,
                x.SharedByName,
                x.Link.SharedAt,
                x.Link.Note))
            .ToList();
    }

    public async Task<IReadOnlyList<ClassroomBoardMessage>> BoardAsync(
        Guid classroomId,
        CancellationToken cancellationToken)
    {
        return await _context.ClassroomMessages
            .AsNoTracking()
            .Where(m => m.ClassroomId == classroomId && m.Kind == ClassroomMessageKind.Announcement && !m.IsDeleted)
            .Join(_context.Users.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => new { m, AuthorName = u.UserName ?? u.Email ?? "Unknown" })
            .OrderByDescending(x => x.m.IsPinned)
            .ThenByDescending(x => x.m.CreatedAt)
            .Select(x => new ClassroomBoardMessage(
                x.m.Id,
                x.m.UserId,
                x.AuthorName,
                x.m.Body,
                x.m.IsPinned,
                x.m.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<int> UnreadChatCountAsync(
        ClassroomMembership membership,
        CancellationToken cancellationToken)
    {
        var since = membership.LastChatReadAt ?? DateTimeOffset.MinValue;
        return await _context.ClassroomMessages
            .CountAsync(m => m.ClassroomId == membership.ClassroomId
                && m.Kind == ClassroomMessageKind.Chat
                && !m.IsDeleted
                && m.UserId != membership.UserId
                && m.CreatedAt > since, cancellationToken);
    }

    public async Task<ClassroomSchedule> ScheduleAsync(
        Guid classroomId,
        string userId,
        CancellationToken cancellationToken)
    {
        var lessons = await _context.ClassroomLessons
            .AsNoTracking()
            .Where(l => l.ClassroomId == classroomId)
            .OrderBy(l => l.ScheduledAt == null)
            .ThenBy(l => l.ScheduledAt)
            .ThenBy(l => l.CreatedAt)
            .ToListAsync(cancellationToken);

        var assignments = await _context.ClassroomAssignments
            .AsNoTracking()
            .Where(a => a.ClassroomId == classroomId)
            .OrderBy(a => a.DueAt == null)
            .ThenBy(a => a.DueAt)
            .ThenBy(a => a.CreatedAt)
            .ToListAsync(cancellationToken);

        var studentIds = await _context.ClassroomMemberships
            .Where(m => m.ClassroomId == classroomId && m.Role == ClassroomRole.Student)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);
        var studentIdSet = studentIds.ToHashSet();
        var studentCount = studentIds.Count;

        var quizIds = assignments.Where(a => a.QuizId.HasValue).Select(a => a.QuizId!.Value).Distinct().ToList();
        var quizNames = quizIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _context.Quizzes.AsNoTracking()
                .Where(q => quizIds.Contains(q.Id))
                .ToDictionaryAsync(q => q.Id, q => q.Name, cancellationToken);

        // One pass over the classroom's attempts for the assigned quizzes covers
        // both the teacher tally and the caller's own completion state.
        var attempts = quizIds.Count == 0
            ? []
            : await _context.QuizAttempts.AsNoTracking()
                .Where(a => a.ClassroomId == classroomId && quizIds.Contains(a.QuizId))
                .Select(a => new { a.QuizId, a.UserId, a.CorrectCount, a.IncorrectCount })
                .ToListAsync(cancellationToken);

        ClassroomAssignmentInfo BuildInfo(ClassroomAssignment assignment)
        {
            if (!assignment.QuizId.HasValue)
            {
                return new ClassroomAssignmentInfo(
                    ClassroomAssignmentHeader.From(assignment), null, 0, studentCount, false, null);
            }

            var quizAttempts = attempts.Where(a => a.QuizId == assignment.QuizId.Value).ToList();
            var completedStudents = quizAttempts
                .Where(a => studentIdSet.Contains(a.UserId))
                .Select(a => a.UserId)
                .Distinct()
                .Count();

            var mine = quizAttempts.Where(a => a.UserId == userId).ToList();
            int? bestScore = mine.Count == 0
                ? null
                : mine.Max(a =>
                {
                    var answered = a.CorrectCount + a.IncorrectCount;
                    return answered == 0 ? 0 : (int)Math.Round(a.CorrectCount * 100d / answered);
                });

            return new ClassroomAssignmentInfo(
                ClassroomAssignmentHeader.From(assignment),
                quizNames.GetValueOrDefault(assignment.QuizId.Value),
                completedStudents,
                studentCount,
                mine.Count > 0,
                bestScore);
        }

        var infosByLesson = assignments
            .Select(BuildInfo)
            .ToLookup(info => info.Assignment.LessonId);

        var lessonInfos = lessons
            .Select(lesson => new ClassroomLessonInfo(
                ClassroomLessonHeader.From(lesson), infosByLesson[lesson.Id].ToList()))
            .ToList();

        return new ClassroomSchedule(lessonInfos, infosByLesson[null].ToList());
    }

    public async Task<IReadOnlyList<ClassroomAttemptRow>> AttemptRowsAsync(
        Guid classroomId,
        string? memberUserId,
        CancellationToken cancellationToken)
    {
        var query = _context.QuizAttempts
            .AsNoTracking()
            .Where(a => a.ClassroomId == classroomId);

        if (memberUserId != null)
        {
            query = query.Where(a => a.UserId == memberUserId);
        }

        return await query
            .Join(_context.Users.AsNoTracking(), a => a.UserId, u => u.Id, (a, u) => new { a, MemberName = u.UserName ?? u.Email ?? "Unknown" })
            .Join(_context.Quizzes.AsNoTracking(), x => x.a.QuizId, q => q.Id, (x, q) => new { x.a, x.MemberName, QuizName = q.Name })
            .OrderByDescending(x => x.a.CompletedAt)
            .Select(x => new ClassroomAttemptRow(
                x.a.Id,
                x.a.UserId,
                x.MemberName,
                x.a.QuizId,
                x.QuizName,
                x.a.Mode,
                x.a.TotalItems,
                x.a.CorrectCount,
                x.a.IncorrectCount,
                x.a.SkippedCount,
                x.a.CompletedAt))
            .ToListAsync(cancellationToken);
    }
}
