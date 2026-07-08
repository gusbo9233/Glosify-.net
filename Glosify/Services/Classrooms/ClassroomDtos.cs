using Glosify.Models.Library;

namespace Glosify.Services.Classrooms;

public sealed record ClassroomSummary(
    Classroom Classroom,
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
    Quiz? Quiz,
    BookDocument? Book,
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
    ClassroomAssignment Assignment,
    string? QuizName,
    int CompletedStudents,
    int TotalStudents,
    bool CompletedByMe,
    int? MyBestScorePercent);

public sealed record ClassroomLessonInfo(
    ClassroomLesson Lesson,
    IReadOnlyList<ClassroomAssignmentInfo> Assignments);

public sealed record ClassroomSchedule(
    IReadOnlyList<ClassroomLessonInfo> Lessons,
    IReadOnlyList<ClassroomAssignmentInfo> UnattachedAssignments);

public sealed record ClassroomDetailsPage(
    Classroom Classroom,
    ClassroomMembership Membership,
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
