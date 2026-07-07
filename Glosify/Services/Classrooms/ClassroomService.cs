using System.Security.Cryptography;
using Glosify.Data;
using Glosify.Models.Library;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Classrooms;

public class ClassroomService : IClassroomService
{
    // No 0/O/1/I so codes read unambiguously when written on a whiteboard.
    private const string JoinCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int JoinCodeLength = 8;
    private const int AnnouncementMaxLength = 4000;

    private readonly GlosifyContext _context;

    public ClassroomService(GlosifyContext context)
    {
        _context = context;
    }

    public async Task<Classroom> CreateAsync(string ownerUserId, string name, string? description, CancellationToken cancellationToken = default)
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
        return classroom;
    }

    public async Task<IReadOnlyList<ClassroomSummary>> GetForUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        return await _context.ClassroomMemberships
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Join(
                _context.Classrooms.AsNoTracking(),
                m => m.ClassroomId,
                c => c.Id,
                (m, c) => new { Membership = m, Classroom = c })
            .OrderByDescending(x => x.Classroom.CreatedAt)
            .Select(x => new ClassroomSummary(
                x.Classroom,
                x.Membership.Role,
                _context.ClassroomMemberships.Count(o => o.ClassroomId == x.Classroom.Id)))
            .ToListAsync(cancellationToken);
    }

    public async Task<Classroom> GetDetailsAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default)
    {
        await RequireMemberAsync(classroomId, userId, cancellationToken);

        return await _context.Classrooms
            .AsNoTracking()
            .FirstAsync(c => c.Id == classroomId, cancellationToken);
    }

    public async Task DeleteClassroomAsync(Guid classroomId, string ownerUserId, CancellationToken cancellationToken = default)
    {
        await RequireOwnerAsync(classroomId, ownerUserId, cancellationToken);

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

    public async Task<Classroom?> JoinByCodeAsync(string userId, string code, CancellationToken cancellationToken = default)
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

        if (!await IsMemberAsync(classroom.Id, userId, cancellationToken))
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

        return classroom;
    }

    public async Task<string> RegenerateJoinCodeAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default)
    {
        await RequireOwnerAsync(classroomId, userId, cancellationToken);

        var classroom = await _context.Classrooms.FirstAsync(c => c.Id == classroomId, cancellationToken);
        classroom.JoinCode = await GenerateUniqueJoinCodeAsync(cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        return classroom.JoinCode;
    }

    public async Task SetJoinCodeEnabledAsync(Guid classroomId, string userId, bool enabled, CancellationToken cancellationToken = default)
    {
        await RequireOwnerAsync(classroomId, userId, cancellationToken);

        var classroom = await _context.Classrooms.FirstAsync(c => c.Id == classroomId, cancellationToken);
        classroom.JoinCodeEnabled = enabled;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task InviteByEmailAsync(Guid classroomId, string userId, string email, ClassroomRole role, CancellationToken cancellationToken = default)
    {
        await RequireTeacherAsync(classroomId, userId, cancellationToken);

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

    public async Task<Classroom?> AcceptInvitationAsync(Guid invitationId, string userId, CancellationToken cancellationToken = default)
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

        if (!await IsMemberAsync(invitation.ClassroomId, userId, cancellationToken))
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

        return await _context.Classrooms
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == invitation.ClassroomId, cancellationToken);
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
        await RequireMemberAsync(classroomId, userId, cancellationToken);

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

    public async Task RemoveMemberAsync(Guid classroomId, string requesterUserId, string memberUserId, CancellationToken cancellationToken = default)
    {
        var requester = await RequireTeacherAsync(classroomId, requesterUserId, cancellationToken);

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
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task ChangeRoleAsync(Guid classroomId, string ownerUserId, string memberUserId, ClassroomRole role, CancellationToken cancellationToken = default)
    {
        await RequireOwnerAsync(classroomId, ownerUserId, cancellationToken);

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

    public async Task ShareQuizAsync(Guid classroomId, string userId, Guid quizId, CancellationToken cancellationToken = default)
    {
        await RequireTeacherAsync(classroomId, userId, cancellationToken);

        var ownsQuiz = await _context.Quizzes.AnyAsync(q => q.Id == quizId && q.UserId == userId, cancellationToken);
        if (!ownsQuiz)
        {
            throw new ClassroomAccessDeniedException("You can only share quizzes you own.");
        }

        var alreadyShared = await _context.ClassroomContents
            .AnyAsync(c => c.ClassroomId == classroomId && c.QuizId == quizId, cancellationToken);
        if (alreadyShared)
        {
            return;
        }

        _context.ClassroomContents.Add(new ClassroomContent
        {
            Id = Guid.NewGuid(),
            ClassroomId = classroomId,
            ContentType = ClassroomContentType.Quiz,
            QuizId = quizId,
            SharedByUserId = userId,
            SharedAt = DateTimeOffset.UtcNow
        });
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task ShareBookAsync(Guid classroomId, string userId, Guid bookDocumentId, CancellationToken cancellationToken = default)
    {
        await RequireTeacherAsync(classroomId, userId, cancellationToken);

        var ownsBook = await _context.BookDocuments.AnyAsync(b => b.Id == bookDocumentId && b.UserId == userId, cancellationToken);
        if (!ownsBook)
        {
            throw new ClassroomAccessDeniedException("You can only share books you own.");
        }

        var alreadyShared = await _context.ClassroomContents
            .AnyAsync(c => c.ClassroomId == classroomId && c.BookDocumentId == bookDocumentId, cancellationToken);
        if (alreadyShared)
        {
            return;
        }

        _context.ClassroomContents.Add(new ClassroomContent
        {
            Id = Guid.NewGuid(),
            ClassroomId = classroomId,
            ContentType = ClassroomContentType.Book,
            BookDocumentId = bookDocumentId,
            SharedByUserId = userId,
            SharedAt = DateTimeOffset.UtcNow
        });
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task UnshareContentAsync(Guid classroomId, string userId, Guid contentId, CancellationToken cancellationToken = default)
    {
        await RequireTeacherAsync(classroomId, userId, cancellationToken);

        var content = await _context.ClassroomContents
            .FirstOrDefaultAsync(c => c.Id == contentId && c.ClassroomId == classroomId, cancellationToken);

        if (content != null)
        {
            _context.ClassroomContents.Remove(content);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<ClassroomContentItem>> GetContentAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default)
    {
        await RequireMemberAsync(classroomId, userId, cancellationToken);

        var links = await _context.ClassroomContents
            .AsNoTracking()
            .Where(c => c.ClassroomId == classroomId)
            .Join(_context.Users.AsNoTracking(), c => c.SharedByUserId, u => u.Id, (c, u) => new { Link = c, SharedByName = u.UserName ?? u.Email ?? "Unknown" })
            .OrderByDescending(x => x.Link.SharedAt)
            .ToListAsync(cancellationToken);

        var quizIds = links.Where(x => x.Link.QuizId.HasValue).Select(x => x.Link.QuizId!.Value).ToList();
        var bookIds = links.Where(x => x.Link.BookDocumentId.HasValue).Select(x => x.Link.BookDocumentId!.Value).ToList();

        var quizzes = quizIds.Count == 0
            ? new Dictionary<Guid, Quiz>()
            : await _context.Quizzes.AsNoTracking().Where(q => quizIds.Contains(q.Id)).ToDictionaryAsync(q => q.Id, cancellationToken);
        var books = bookIds.Count == 0
            ? new Dictionary<Guid, BookDocument>()
            : await _context.BookDocuments.AsNoTracking().Where(b => bookIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, cancellationToken);

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

    public async Task<Quiz> RequireSharedQuizAsync(Guid classroomId, Guid quizId, string userId, CancellationToken cancellationToken = default)
    {
        await RequireMemberAsync(classroomId, userId, cancellationToken);

        var isShared = await _context.ClassroomContents
            .AnyAsync(c => c.ClassroomId == classroomId && c.QuizId == quizId, cancellationToken);
        if (!isShared)
        {
            throw new ClassroomAccessDeniedException("That quiz is not shared in this classroom.");
        }

        return await _context.Quizzes
            .AsNoTracking()
            .FirstAsync(q => q.Id == quizId, cancellationToken);
    }

    public async Task<BookDocument> RequireSharedBookAsync(Guid classroomId, Guid bookDocumentId, string userId, CancellationToken cancellationToken = default)
    {
        await RequireMemberAsync(classroomId, userId, cancellationToken);

        var isShared = await _context.ClassroomContents
            .AnyAsync(c => c.ClassroomId == classroomId && c.BookDocumentId == bookDocumentId, cancellationToken);
        if (!isShared)
        {
            throw new ClassroomAccessDeniedException("That book is not shared in this classroom.");
        }

        return await _context.BookDocuments
            .AsNoTracking()
            .FirstAsync(b => b.Id == bookDocumentId, cancellationToken);
    }

    public async Task PostAnnouncementAsync(Guid classroomId, string userId, string body, CancellationToken cancellationToken = default)
    {
        await RequireTeacherAsync(classroomId, userId, cancellationToken);

        body = body.Trim();
        if (body.Length == 0)
        {
            throw new ArgumentException("Write a message before posting.");
        }

        if (body.Length > AnnouncementMaxLength)
        {
            throw new ArgumentException($"Announcements are limited to {AnnouncementMaxLength} characters.");
        }

        _context.ClassroomMessages.Add(new ClassroomMessage
        {
            Id = Guid.NewGuid(),
            ClassroomId = classroomId,
            UserId = userId,
            Kind = ClassroomMessageKind.Announcement,
            Body = body,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ClassroomBoardMessage>> GetBoardAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default)
    {
        await RequireMemberAsync(classroomId, userId, cancellationToken);

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

    public async Task SetPinnedAsync(Guid classroomId, string userId, Guid messageId, bool pinned, CancellationToken cancellationToken = default)
    {
        await RequireTeacherAsync(classroomId, userId, cancellationToken);

        var message = await _context.ClassroomMessages
            .FirstOrDefaultAsync(m => m.Id == messageId && m.ClassroomId == classroomId, cancellationToken);

        if (message != null)
        {
            message.IsPinned = pinned;
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task DeleteMessageAsync(Guid classroomId, string userId, Guid messageId, CancellationToken cancellationToken = default)
    {
        var membership = await RequireMemberAsync(classroomId, userId, cancellationToken);

        var message = await _context.ClassroomMessages
            .FirstOrDefaultAsync(m => m.Id == messageId && m.ClassroomId == classroomId, cancellationToken);

        if (message == null)
        {
            return;
        }

        var isTeacher = membership.Role is ClassroomRole.Owner or ClassroomRole.Teacher;
        if (!isTeacher && message.UserId != userId)
        {
            throw new ClassroomAccessDeniedException("You can only delete your own messages.");
        }

        message.IsDeleted = true;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ClassroomAttemptRow>> GetClassroomResultsAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default)
    {
        await RequireTeacherAsync(classroomId, userId, cancellationToken);
        return await QueryAttemptRowsAsync(classroomId, memberUserId: null, cancellationToken);
    }

    public async Task<IReadOnlyList<ClassroomAttemptRow>> GetMemberResultsAsync(Guid classroomId, string requesterUserId, string memberUserId, CancellationToken cancellationToken = default)
    {
        if (requesterUserId == memberUserId)
        {
            await RequireMemberAsync(classroomId, requesterUserId, cancellationToken);
        }
        else
        {
            await RequireTeacherAsync(classroomId, requesterUserId, cancellationToken);
        }

        return await QueryAttemptRowsAsync(classroomId, memberUserId, cancellationToken);
    }

    private async Task<IReadOnlyList<ClassroomAttemptRow>> QueryAttemptRowsAsync(Guid classroomId, string? memberUserId, CancellationToken cancellationToken)
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
