using Glosify.Models.Library;

namespace Glosify.Services.Classrooms;

public interface IClassroomService
{
    Task<Classroom> CreateAsync(string ownerUserId, string name, string? description, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClassroomSummary>> GetForUserAsync(string userId, CancellationToken cancellationToken = default);
    Task<Classroom> GetDetailsAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);
    Task DeleteClassroomAsync(Guid classroomId, string ownerUserId, CancellationToken cancellationToken = default);

    Task<ClassroomMembership> RequireMemberAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);
    Task<ClassroomMembership> RequireTeacherAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);
    Task<ClassroomMembership> RequireOwnerAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);
    Task<bool> IsMemberAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);

    Task<Classroom?> JoinByCodeAsync(string userId, string code, CancellationToken cancellationToken = default);
    Task<string> RegenerateJoinCodeAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);
    Task SetJoinCodeEnabledAsync(Guid classroomId, string userId, bool enabled, CancellationToken cancellationToken = default);

    Task InviteByEmailAsync(Guid classroomId, string userId, string email, ClassroomRole role, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PendingInvitationInfo>> GetPendingInvitationsForUserAsync(string userId, CancellationToken cancellationToken = default);
    Task<Classroom?> AcceptInvitationAsync(Guid invitationId, string userId, CancellationToken cancellationToken = default);
    Task DeclineInvitationAsync(Guid invitationId, string userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ClassroomMemberInfo>> GetMembersAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);
    Task RemoveMemberAsync(Guid classroomId, string requesterUserId, string memberUserId, CancellationToken cancellationToken = default);
    Task LeaveAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);
    Task ChangeRoleAsync(Guid classroomId, string ownerUserId, string memberUserId, ClassroomRole role, CancellationToken cancellationToken = default);

    Task ShareQuizAsync(Guid classroomId, string userId, Guid quizId, CancellationToken cancellationToken = default);
    Task ShareBookAsync(Guid classroomId, string userId, Guid bookDocumentId, CancellationToken cancellationToken = default);
    Task UnshareContentAsync(Guid classroomId, string userId, Guid contentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClassroomContentItem>> GetContentAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);
    Task<Quiz> RequireSharedQuizAsync(Guid classroomId, Guid quizId, string userId, CancellationToken cancellationToken = default);
    Task<BookDocument> RequireSharedBookAsync(Guid classroomId, Guid bookDocumentId, string userId, CancellationToken cancellationToken = default);

    Task PostAnnouncementAsync(Guid classroomId, string userId, string body, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClassroomBoardMessage>> GetBoardAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);
    Task SetPinnedAsync(Guid classroomId, string userId, Guid messageId, bool pinned, CancellationToken cancellationToken = default);
    Task DeleteMessageAsync(Guid classroomId, string userId, Guid messageId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ClassroomAttemptRow>> GetClassroomResultsAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClassroomAttemptRow>> GetMemberResultsAsync(Guid classroomId, string requesterUserId, string memberUserId, CancellationToken cancellationToken = default);
}
