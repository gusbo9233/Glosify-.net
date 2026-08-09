using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Classrooms;

/// <summary>
/// Who is in a classroom and how they got there: invitations, membership, and roles.
/// </summary>
public interface IClassroomRoster
{
    Task InviteByEmailAsync(Guid classroomId, string userId, string email, ClassroomRole role, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PendingInvitationInfo>> GetPendingInvitationsForUserAsync(string userId, CancellationToken cancellationToken = default);
    Task<ClassroomHeader?> AcceptInvitationAsync(Guid invitationId, string userId, CancellationToken cancellationToken = default);
    Task DeclineInvitationAsync(Guid invitationId, string userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ClassroomMemberInfo>> GetMembersAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);
    Task<string?> GetMemberNameAsync(Guid classroomId, string requesterUserId, string memberUserId, CancellationToken cancellationToken = default);
    Task RemoveMemberAsync(Guid classroomId, string requesterUserId, string memberUserId, CancellationToken cancellationToken = default);
    Task LeaveAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);
    Task ChangeRoleAsync(Guid classroomId, string ownerUserId, string memberUserId, ClassroomRole role, CancellationToken cancellationToken = default);
}

public sealed class ClassroomRoster : IClassroomRoster
{
    private readonly GlosifyContext _context;
    private readonly IClassroomAccess _access;
    private readonly ClassroomQueries _queries;

    public ClassroomRoster(
        GlosifyContext context,
        IClassroomAccess access,
        ClassroomQueries queries)
    {
        _context = context;
        _access = access;
        _queries = queries;
    }

    public async Task InviteByEmailAsync(Guid classroomId, string userId, string email, ClassroomRole role, CancellationToken cancellationToken = default)
    {
        await _access.RequireTeacherAsync(classroomId, userId, cancellationToken);

        if (role == ClassroomRole.Owner)
        {
            throw new ArgumentException("Invitations can only grant the teacher or student role.");
        }

        email = NormalizeEmail(email);
        if (email.Length == 0 || !email.Contains('@'))
        {
            throw new ArgumentException("Enter a valid email address.");
        }

        var alreadyMember = await _context.ClassroomMemberships
            .Join(_context.Users, m => m.UserId, u => u.Id, (m, u) => new { m.ClassroomId, u.NormalizedEmail })
            .AnyAsync(x => x.ClassroomId == classroomId && x.NormalizedEmail == email, cancellationToken);
        if (alreadyMember)
        {
            throw new ArgumentException("That user is already a member of this classroom.");
        }

        var pending = await _context.ClassroomInvitations
            .FirstOrDefaultAsync(i => i.ClassroomId == classroomId && i.Email == email && i.AcceptedAt == null, cancellationToken);
        if (pending != null)
        {
            pending.Role = role;
        }
        else
        {
            _context.ClassroomInvitations.Add(new ClassroomInvitation
            {
                Id = Guid.NewGuid(),
                ClassroomId = classroomId,
                Email = email,
                InvitedByUserId = userId,
                Role = role,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PendingInvitationInfo>> GetPendingInvitationsForUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        var email = await GetUserNormalizedEmailAsync(userId, cancellationToken);
        if (email.Length == 0)
        {
            return [];
        }

        return await _context.ClassroomInvitations
            .AsNoTracking()
            .Where(i => i.Email == email && i.AcceptedAt == null)
            .Join(_context.Classrooms.AsNoTracking(), i => i.ClassroomId, c => c.Id, (i, c) => new { i, c })
            .Join(_context.Users.AsNoTracking(), x => x.i.InvitedByUserId, u => u.Id, (x, u) => new { x.i, x.c, Inviter = u })
            .OrderByDescending(x => x.i.CreatedAt)
            .Select(x => new PendingInvitationInfo(
                x.i.Id,
                x.c.Id,
                x.c.Name,
                x.Inviter.Email ?? x.Inviter.UserName ?? "Unknown",
                x.i.Role,
                x.i.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<ClassroomHeader?> AcceptInvitationAsync(Guid invitationId, string userId, CancellationToken cancellationToken = default)
    {
        var email = await GetUserNormalizedEmailAsync(userId, cancellationToken);
        var invitation = await _context.ClassroomInvitations
            .FirstOrDefaultAsync(i => i.Id == invitationId && i.Email == email && i.AcceptedAt == null, cancellationToken);

        if (invitation == null)
        {
            return null;
        }

        invitation.AcceptedAt = DateTimeOffset.UtcNow;
        invitation.AcceptedByUserId = userId;

        if (!await _access.IsMemberAsync(invitation.ClassroomId, userId, cancellationToken))
        {
            _context.ClassroomMemberships.Add(new ClassroomMembership
            {
                Id = Guid.NewGuid(),
                ClassroomId = invitation.ClassroomId,
                UserId = userId,
                Role = invitation.Role,
                JoinedAt = DateTimeOffset.UtcNow
            });
        }

        await _context.SaveChangesAsync(cancellationToken);

        var classroom = await _context.Classrooms
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == invitation.ClassroomId, cancellationToken);

        return classroom is null ? null : ClassroomHeader.From(classroom);
    }

    public async Task DeclineInvitationAsync(Guid invitationId, string userId, CancellationToken cancellationToken = default)
    {
        var email = await GetUserNormalizedEmailAsync(userId, cancellationToken);
        var invitation = await _context.ClassroomInvitations
            .FirstOrDefaultAsync(i => i.Id == invitationId && i.Email == email && i.AcceptedAt == null, cancellationToken);

        if (invitation != null)
        {
            _context.ClassroomInvitations.Remove(invitation);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<ClassroomMemberInfo>> GetMembersAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default)
    {
        await _access.RequireMemberAsync(classroomId, userId, cancellationToken);
        return await _queries.MembersAsync(classroomId, cancellationToken);
    }

    public async Task<string?> GetMemberNameAsync(Guid classroomId, string requesterUserId, string memberUserId, CancellationToken cancellationToken = default)
    {
        await _access.RequireMemberAsync(classroomId, requesterUserId, cancellationToken);

        return await _context.ClassroomMemberships
            .AsNoTracking()
            .Where(m => m.ClassroomId == classroomId && m.UserId == memberUserId)
            .Join(_context.Users.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => u.UserName ?? u.Email ?? "Unknown")
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task RemoveMemberAsync(Guid classroomId, string requesterUserId, string memberUserId, CancellationToken cancellationToken = default)
    {
        var requester = await _access.RequireTeacherAsync(classroomId, requesterUserId, cancellationToken);

        var membership = await _context.ClassroomMemberships
            .FirstOrDefaultAsync(m => m.ClassroomId == classroomId && m.UserId == memberUserId, cancellationToken)
            ?? throw new ClassroomAccessDeniedException("That user is not a member of this classroom.");

        if (membership.Role == ClassroomRole.Owner)
        {
            throw new ClassroomAccessDeniedException("The classroom owner cannot be removed.");
        }

        if (membership.Role == ClassroomRole.Teacher && requester.Role != ClassroomRole.Owner)
        {
            throw new ClassroomAccessDeniedException("Only the classroom owner can remove a teacher.");
        }

        _context.ClassroomMemberships.Remove(membership);
        await RotateGroupCallIdAsync(classroomId, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task LeaveAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default)
    {
        var membership = await _context.ClassroomMemberships
            .FirstOrDefaultAsync(m => m.ClassroomId == classroomId && m.UserId == userId, cancellationToken)
            ?? throw new ClassroomAccessDeniedException();

        if (membership.Role == ClassroomRole.Owner)
        {
            throw new ClassroomAccessDeniedException("The owner cannot leave; delete the classroom instead.");
        }

        _context.ClassroomMemberships.Remove(membership);
        await RotateGroupCallIdAsync(classroomId, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    // ACS group calls have no server-side roster; the group id is the only
    // gate. Rotate it when someone leaves so an ex-member who captured the id
    // cannot rejoin future calls. Participants already in a call stay on the
    // old id until they rejoin.
    private async Task RotateGroupCallIdAsync(Guid classroomId, CancellationToken cancellationToken)
    {
        var classroom = await _context.Classrooms.FirstAsync(c => c.Id == classroomId, cancellationToken);
        classroom.GroupCallId = Guid.NewGuid();
    }

    public async Task ChangeRoleAsync(Guid classroomId, string ownerUserId, string memberUserId, ClassroomRole role, CancellationToken cancellationToken = default)
    {
        await _access.RequireOwnerAsync(classroomId, ownerUserId, cancellationToken);

        if (role == ClassroomRole.Owner)
        {
            throw new ArgumentException("Ownership cannot be transferred here.");
        }

        var membership = await _context.ClassroomMemberships
            .FirstOrDefaultAsync(m => m.ClassroomId == classroomId && m.UserId == memberUserId, cancellationToken)
            ?? throw new ClassroomAccessDeniedException("That user is not a member of this classroom.");

        if (membership.Role == ClassroomRole.Owner)
        {
            throw new ClassroomAccessDeniedException("The owner's role cannot be changed.");
        }

        membership.Role = role;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<string> GetUserNormalizedEmailAsync(string userId, CancellationToken cancellationToken)
    {
        var email = await _context.Users
            .Where(u => u.Id == userId)
            .Select(u => u.NormalizedEmail)
            .FirstOrDefaultAsync(cancellationToken);

        return email ?? string.Empty;
    }

    private static string NormalizeEmail(string email)
    {
        return (email ?? string.Empty).Trim().ToUpperInvariant();
    }
}
