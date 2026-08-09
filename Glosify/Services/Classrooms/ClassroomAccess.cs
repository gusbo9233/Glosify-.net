using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Classrooms;

/// <summary>
/// The role check every other classroom service starts from. Each Require method throws
/// <see cref="ClassroomAccessDeniedException"/> rather than returning null, so forgetting
/// to check the result cannot silently grant access.
/// </summary>
public interface IClassroomAccess
{
    Task<ClassroomMembership> RequireMemberAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);
    Task<ClassroomMembership> RequireTeacherAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);
    Task<ClassroomMembership> RequireOwnerAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);
    Task<bool> IsMemberAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);
}

public sealed class ClassroomAccess : IClassroomAccess
{
    private readonly GlosifyContext _context;

    public ClassroomAccess(GlosifyContext context)
    {
        _context = context;
    }

    public async Task<ClassroomMembership> RequireMemberAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default)
    {
        var membership = await _context.ClassroomMemberships
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.ClassroomId == classroomId && m.UserId == userId, cancellationToken);

        return membership ?? throw new ClassroomAccessDeniedException();
    }

    public async Task<ClassroomMembership> RequireTeacherAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default)
    {
        var membership = await RequireMemberAsync(classroomId, userId, cancellationToken);
        if (membership.Role is not (ClassroomRole.Owner or ClassroomRole.Teacher))
        {
            throw new ClassroomAccessDeniedException("Only teachers can do that.");
        }

        return membership;
    }

    public async Task<ClassroomMembership> RequireOwnerAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default)
    {
        var membership = await RequireMemberAsync(classroomId, userId, cancellationToken);
        if (membership.Role != ClassroomRole.Owner)
        {
            throw new ClassroomAccessDeniedException("Only the classroom owner can do that.");
        }

        return membership;
    }

    public async Task<bool> IsMemberAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default)
    {
        return await _context.ClassroomMemberships
            .AnyAsync(m => m.ClassroomId == classroomId && m.UserId == userId, cancellationToken);
    }
}
