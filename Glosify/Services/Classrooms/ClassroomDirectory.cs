using System.Security.Cryptography;
using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Classrooms;

/// <summary>
/// Classrooms themselves: creating, listing, opening, deleting, and the join code that
/// lets someone in.
/// </summary>
public interface IClassroomDirectory
{
    Task<ClassroomHeader> CreateAsync(string ownerUserId, string name, string? description, string? language = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the user's classrooms, limited to <paramref name="language"/> when one
    /// is selected. Classrooms without a language stay visible in every language.
    /// </summary>
    Task<IReadOnlyList<ClassroomSummary>> GetForUserAsync(string userId, string? language = null, CancellationToken cancellationToken = default);

    Task<ClassroomHeader> GetDetailsAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);
    /// <summary>
    /// The ACS group id for the classroom's call. Deliberately not on
    /// <see cref="ClassroomHeader"/>: it is call plumbing, and putting it on the record
    /// every classroom page renders would leak it into markup that has no use for it.
    /// </summary>
    Task<Guid> GetGroupCallIdAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);

    Task<ClassroomDetailsPage> GetDetailsPageAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);
    Task DeleteClassroomAsync(Guid classroomId, string ownerUserId, CancellationToken cancellationToken = default);

    Task<ClassroomHeader?> JoinByCodeAsync(string userId, string code, CancellationToken cancellationToken = default);
    Task<string> RegenerateJoinCodeAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);
    Task SetJoinCodeEnabledAsync(Guid classroomId, string userId, bool enabled, CancellationToken cancellationToken = default);
}

public sealed class ClassroomDirectory : IClassroomDirectory
{
    // No 0/O/1/I so codes read unambiguously when written on a whiteboard.
    private const string JoinCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int JoinCodeLength = 8;

    private readonly GlosifyContext _context;
    private readonly IClassroomAccess _access;
    private readonly ClassroomQueries _queries;

    public ClassroomDirectory(
        GlosifyContext context,
        IClassroomAccess access,
        ClassroomQueries queries)
    {
        _context = context;
        _access = access;
        _queries = queries;
    }

    public async Task<ClassroomHeader> CreateAsync(string ownerUserId, string name, string? description, string? language = null, CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Give the classroom a name.");
        }

        var classroom = new Classroom
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Name = name,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            Language = string.IsNullOrWhiteSpace(language) ? null : language,
            JoinCode = await GenerateUniqueJoinCodeAsync(cancellationToken),
            JoinCodeEnabled = true,
            GroupCallId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        _context.Classrooms.Add(classroom);
        _context.ClassroomMemberships.Add(new ClassroomMembership
        {
            Id = Guid.NewGuid(),
            ClassroomId = classroom.Id,
            UserId = ownerUserId,
            Role = ClassroomRole.Owner,
            JoinedAt = classroom.CreatedAt
        });

        await _context.SaveChangesAsync(cancellationToken);
        return ClassroomHeader.From(classroom);
    }

    public async Task<IReadOnlyList<ClassroomSummary>> GetForUserAsync(string userId, string? language = null, CancellationToken cancellationToken = default)
    {
        // Classrooms created before a language was picked stay visible in every
        // language: a shared space should never vanish for the people in it.
        var classrooms = _context.Classrooms.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(language))
        {
            classrooms = classrooms.Where(c => c.Language == null || c.Language == language);
        }

        return await _context.ClassroomMemberships
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Join(
                classrooms,
                m => m.ClassroomId,
                c => c.Id,
                (m, c) => new { Membership = m, Classroom = c })
            .OrderByDescending(x => x.Classroom.CreatedAt)
            .Select(x => new ClassroomSummary(
                new ClassroomHeader(
                    x.Classroom.Id,
                    x.Classroom.Name,
                    x.Classroom.Description,
                    x.Classroom.Language,
                    x.Classroom.JoinCode,
                    x.Classroom.JoinCodeEnabled),
                x.Membership.Role,
                _context.ClassroomMemberships.Count(o => o.ClassroomId == x.Classroom.Id)))
            .ToListAsync(cancellationToken);
    }

    public async Task<ClassroomHeader> GetDetailsAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default)
    {
        await _access.RequireMemberAsync(classroomId, userId, cancellationToken);

        var classroom = await _context.Classrooms
            .AsNoTracking()
            .FirstAsync(c => c.Id == classroomId, cancellationToken);

        return ClassroomHeader.From(classroom);
    }

    public async Task<Guid> GetGroupCallIdAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default)
    {
        await _access.RequireMemberAsync(classroomId, userId, cancellationToken);

        return await _context.Classrooms
            .AsNoTracking()
            .Where(c => c.Id == classroomId)
            .Select(c => c.GroupCallId)
            .FirstAsync(cancellationToken);
    }

    public async Task<ClassroomDetailsPage> GetDetailsPageAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default)
    {
        // One membership check covers every query on the details page.
        var membership = await _access.RequireMemberAsync(classroomId, userId, cancellationToken);
        var classroom = await _context.Classrooms
            .AsNoTracking()
            .FirstAsync(c => c.Id == classroomId, cancellationToken);

        return new ClassroomDetailsPage(
            ClassroomHeader.From(classroom),
            membership.Role,
            await _queries.BoardAsync(classroomId, cancellationToken),
            await _queries.MembersAsync(classroomId, cancellationToken),
            await _queries.ContentAsync(classroomId, cancellationToken),
            await _queries.UnreadChatCountAsync(membership, cancellationToken),
            await _queries.ScheduleAsync(classroomId, userId, cancellationToken));
    }

    public async Task DeleteClassroomAsync(Guid classroomId, string ownerUserId, CancellationToken cancellationToken = default)
    {
        await _access.RequireOwnerAsync(classroomId, ownerUserId, cancellationToken);

        var classroom = await _context.Classrooms
            .FirstAsync(c => c.Id == classroomId, cancellationToken);

        // QuizAttempt.ClassroomId is NoAction (SQL Server cascade-path limits), so
        // detach attempts before the classroom row goes; attempts stay as personal history.
        var attempts = await _context.QuizAttempts
            .Where(a => a.ClassroomId == classroomId)
            .ToListAsync(cancellationToken);
        foreach (var attempt in attempts)
        {
            attempt.ClassroomId = null;
        }

        _context.Classrooms.Remove(classroom);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<ClassroomHeader?> JoinByCodeAsync(string userId, string code, CancellationToken cancellationToken = default)
    {
        code = code.Trim().ToUpperInvariant();
        if (code.Length == 0)
        {
            return null;
        }

        var classroom = await _context.Classrooms
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.JoinCode == code && c.JoinCodeEnabled && !c.IsArchived, cancellationToken);

        if (classroom == null)
        {
            return null;
        }

        if (!await _access.IsMemberAsync(classroom.Id, userId, cancellationToken))
        {
            _context.ClassroomMemberships.Add(new ClassroomMembership
            {
                Id = Guid.NewGuid(),
                ClassroomId = classroom.Id,
                UserId = userId,
                Role = ClassroomRole.Student,
                JoinedAt = DateTimeOffset.UtcNow
            });
            await _context.SaveChangesAsync(cancellationToken);
        }

        return ClassroomHeader.From(classroom);
    }

    public async Task<string> RegenerateJoinCodeAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default)
    {
        await _access.RequireOwnerAsync(classroomId, userId, cancellationToken);

        var classroom = await _context.Classrooms.FirstAsync(c => c.Id == classroomId, cancellationToken);
        classroom.JoinCode = await GenerateUniqueJoinCodeAsync(cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        return classroom.JoinCode;
    }

    public async Task SetJoinCodeEnabledAsync(Guid classroomId, string userId, bool enabled, CancellationToken cancellationToken = default)
    {
        await _access.RequireOwnerAsync(classroomId, userId, cancellationToken);

        var classroom = await _context.Classrooms.FirstAsync(c => c.Id == classroomId, cancellationToken);
        classroom.JoinCodeEnabled = enabled;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<string> GenerateUniqueJoinCodeAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var code = GenerateJoinCode();
            if (!await _context.Classrooms.AnyAsync(c => c.JoinCode == code, cancellationToken))
            {
                return code;
            }
        }

        throw new InvalidOperationException("Could not generate a unique join code.");
    }

    private static string GenerateJoinCode()
    {
        Span<char> chars = stackalloc char[JoinCodeLength];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = JoinCodeAlphabet[RandomNumberGenerator.GetInt32(JoinCodeAlphabet.Length)];
        }

        return new string(chars);
    }
}
