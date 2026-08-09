namespace Glosify.Services.Classrooms;

/// <summary>
/// Quiz attempts made inside a classroom: the teacher's whole-class view, and one member's.
/// </summary>
public interface IClassroomResults
{
    Task<IReadOnlyList<ClassroomAttemptRow>> GetClassroomResultsAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClassroomAttemptRow>> GetMemberResultsAsync(Guid classroomId, string requesterUserId, string memberUserId, CancellationToken cancellationToken = default);
}

public sealed class ClassroomResults : IClassroomResults
{
    private readonly IClassroomAccess _access;
    private readonly ClassroomQueries _queries;

    public ClassroomResults(IClassroomAccess access, ClassroomQueries queries)
    {
        _access = access;
        _queries = queries;
    }

    public async Task<IReadOnlyList<ClassroomAttemptRow>> GetClassroomResultsAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default)
    {
        await _access.RequireTeacherAsync(classroomId, userId, cancellationToken);
        return await _queries.AttemptRowsAsync(classroomId, memberUserId: null, cancellationToken);
    }

    public async Task<IReadOnlyList<ClassroomAttemptRow>> GetMemberResultsAsync(Guid classroomId, string requesterUserId, string memberUserId, CancellationToken cancellationToken = default)
    {
        // Anyone may read their own results; reading someone else's takes a teacher.
        if (requesterUserId == memberUserId)
        {
            await _access.RequireMemberAsync(classroomId, requesterUserId, cancellationToken);
        }
        else
        {
            await _access.RequireTeacherAsync(classroomId, requesterUserId, cancellationToken);
        }

        return await _queries.AttemptRowsAsync(classroomId, memberUserId, cancellationToken);
    }
}
