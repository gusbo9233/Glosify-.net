using Glosify.Models.Library;
using Glosify.Services.Classrooms;

namespace Glosify.Models.ViewModels;

public class ClassroomIndexViewModel
{
    public IReadOnlyList<ClassroomSummary> Classrooms { get; set; } = [];
    public IReadOnlyList<PendingInvitationInfo> PendingInvitations { get; set; } = [];
}

public class ClassroomDetailsViewModel
{
    public Classroom Classroom { get; set; } = null!;
    public ClassroomRole CurrentRole { get; set; }
    public string ActiveTab { get; set; } = "stream";

    public IReadOnlyList<ClassroomBoardMessage> Board { get; set; } = [];
    public IReadOnlyList<ClassroomMemberInfo> Members { get; set; } = [];
    public IReadOnlyList<ClassroomContentItem> Content { get; set; } = [];
    public IReadOnlyList<ClassroomAttemptRow> Results { get; set; } = [];

    // Share pickers for teachers: their own quizzes/books not yet shared here.
    public IReadOnlyList<Quiz> ShareableQuizzes { get; set; } = [];
    public IReadOnlyList<BookDocument> ShareableBooks { get; set; } = [];

    public bool IsTeacher => CurrentRole is ClassroomRole.Owner or ClassroomRole.Teacher;
    public bool IsOwner => CurrentRole == ClassroomRole.Owner;
}

public class ClassroomMemberResultsViewModel
{
    public Classroom Classroom { get; set; } = null!;
    public string MemberUserId { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public IReadOnlyList<ClassroomAttemptRow> Results { get; set; } = [];
}
