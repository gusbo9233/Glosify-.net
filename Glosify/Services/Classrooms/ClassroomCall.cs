using Glosify.Models.Entities;
namespace Glosify.Services.Classrooms;

/// <summary>
/// How many people are currently in a classroom's video call. Backed by the SignalR hub's
/// connection map, behind an interface so the join rule below can be reasoned about — and
/// tested — without a hub.
/// </summary>
public interface IClassroomCallPresence
{
    int GetParticipantCount(Guid classroomId);
}

/// <summary>
/// Raised when a student tries to be the first one into a call.
/// </summary>
public sealed class ClassroomCallNotStartedException : InvalidOperationException
{
    public ClassroomCallNotStartedException()
        : base("Only a teacher can start the call. You can join once a teacher has started it.")
    {
    }
}

public sealed record ClassroomCallEntry(
    Classroom Classroom,
    bool IsTeacher,
    int ActiveParticipants);

/// <summary>
/// Who may enter a classroom's video call.
/// </summary>
public interface IClassroomCall
{
    /// <summary>
    /// Resolves the call a member is allowed to see.
    /// </summary>
    Task<ClassroomCallEntry> GetEntryAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as <see cref="GetEntryAsync"/>, but additionally enforces that only a teacher
    /// may open a call nobody is in yet.
    /// </summary>
    /// <exception cref="ClassroomCallNotStartedException">A student tried to start one.</exception>
    Task<ClassroomCallEntry> RequireJoinableAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);
}

public sealed class ClassroomCall : IClassroomCall
{
    private readonly IClassroomAccess _access;
    private readonly IClassroomDirectory _classrooms;
    private readonly IClassroomCallPresence _presence;

    public ClassroomCall(
        IClassroomAccess access,
        IClassroomDirectory classrooms,
        IClassroomCallPresence presence)
    {
        _access = access;
        _classrooms = classrooms;
        _presence = presence;
    }

    public async Task<ClassroomCallEntry> GetEntryAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default)
    {
        var membership = await _access.RequireMemberAsync(classroomId, userId, cancellationToken);
        var classroom = await _classrooms.GetDetailsAsync(classroomId, userId, cancellationToken);

        return new ClassroomCallEntry(
            classroom,
            membership.Role is ClassroomRole.Owner or ClassroomRole.Teacher,
            _presence.GetParticipantCount(classroomId));
    }

    public async Task<ClassroomCallEntry> RequireJoinableAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default)
    {
        var entry = await GetEntryAsync(classroomId, userId, cancellationToken);

        // Students may only join a call that is already in progress; starting one is
        // reserved for teachers. This is the enforcement point, not the UI.
        if (!entry.IsTeacher && entry.ActiveParticipants == 0)
        {
            throw new ClassroomCallNotStartedException();
        }

        return entry;
    }
}
