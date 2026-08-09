using Glosify.Models.Entities;
using Glosify.Models.Library;

namespace Glosify.Services.Classrooms;

/// <summary>
/// A classroom as the pages that show one need it.
/// </summary>
/// <remarks>
/// These records exist so nothing above the service layer holds an entity. The API models
/// in Models/Api already worked this way; the classroom pages did not, and handed views a
/// tracked <c>Classroom</c>, <c>Quiz</c> and <c>BookDocument</c> instead. Property names
/// deliberately match the entities they replace, so the change is invisible in Razor.
/// </remarks>
public sealed record ClassroomHeader(
    Guid Id,
    string Name,
    string? Description,
    string? Language,
    string JoinCode,
    bool JoinCodeEnabled)
{
    public static ClassroomHeader From(Classroom classroom) => new(
        classroom.Id,
        classroom.Name,
        classroom.Description,
        classroom.Language,
        classroom.JoinCode,
        classroom.JoinCodeEnabled);
}

public sealed record ClassroomQuizRef(Guid Id, string Name, string TargetLanguage)
{
    public static ClassroomQuizRef From(Quiz quiz) => new(quiz.Id, quiz.Name, quiz.TargetLanguage);
}

public sealed record ClassroomBookRef(Guid Id, string Title)
{
    public static ClassroomBookRef From(BookDocument book) => new(book.Id, book.Title);
}

public sealed record ClassroomLessonHeader(
    Guid Id,
    string Title,
    string? Description,
    DateTimeOffset? ScheduledAt)
{
    public static ClassroomLessonHeader From(ClassroomLesson lesson) => new(
        lesson.Id,
        lesson.Title,
        lesson.Description,
        lesson.ScheduledAt);
}

public sealed record ClassroomAssignmentHeader(
    Guid Id,
    Guid? LessonId,
    string Title,
    string? Instructions,
    Guid? QuizId,
    DateTimeOffset? DueAt)
{
    public static ClassroomAssignmentHeader From(ClassroomAssignment assignment) => new(
        assignment.Id,
        assignment.LessonId,
        assignment.Title,
        assignment.Instructions,
        assignment.QuizId,
        assignment.DueAt);
}

public sealed record ClassroomSummary(
    ClassroomHeader Classroom,
    ClassroomRole Role,
    int MemberCount);

public sealed record ClassroomMemberInfo(
    string UserId,
    string DisplayName,
    string Email,
    ClassroomRole Role,
    DateTimeOffset JoinedAt);

public sealed record ClassroomContentItem(
    Guid Id,
    ClassroomContentType ContentType,
    ClassroomQuizRef? Quiz,
    ClassroomBookRef? Book,
    string SharedByName,
    DateTimeOffset SharedAt,
    string? Note);

public sealed record ClassroomChatMessage(
    Guid Id,
    string UserId,
    string AuthorName,
    string Body,
    DateTimeOffset CreatedAt);

public sealed record ClassroomBoardMessage(
    Guid Id,
    string UserId,
    string AuthorName,
    string Body,
    bool IsPinned,
    DateTimeOffset CreatedAt);

public sealed record PendingInvitationInfo(
    Guid Id,
    Guid ClassroomId,
    string ClassroomName,
    string InvitedByName,
    ClassroomRole Role,
    DateTimeOffset CreatedAt);

public sealed record ClassroomAssignmentInfo(
    ClassroomAssignmentHeader Assignment,
    string? QuizName,
    int CompletedStudents,
    int TotalStudents,
    bool CompletedByMe,
    int? MyBestScorePercent);

public sealed record ClassroomLessonInfo(
    ClassroomLessonHeader Lesson,
    IReadOnlyList<ClassroomAssignmentInfo> Assignments);

public sealed record ClassroomSchedule(
    IReadOnlyList<ClassroomLessonInfo> Lessons,
    IReadOnlyList<ClassroomAssignmentInfo> UnattachedAssignments);

public sealed record ClassroomDetailsPage(
    ClassroomHeader Classroom,
    ClassroomRole Role,
    IReadOnlyList<ClassroomBoardMessage> Board,
    IReadOnlyList<ClassroomMemberInfo> Members,
    IReadOnlyList<ClassroomContentItem> Content,
    int UnreadChatCount,
    ClassroomSchedule Schedule);

public sealed record ClassroomAttemptRow(
    Guid AttemptId,
    string UserId,
    string MemberName,
    Guid QuizId,
    string QuizName,
    string Mode,
    int TotalItems,
    int CorrectCount,
    int IncorrectCount,
    int SkippedCount,
    DateTimeOffset CompletedAt);
