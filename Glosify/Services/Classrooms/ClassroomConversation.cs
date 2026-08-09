using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Classrooms;

/// <summary>
/// The two message streams in a classroom: live chat, and the teacher's announcement board.
/// They share a table and a moderation rule, which is why they share a service.
/// </summary>
public interface IClassroomConversation
{
    Task<ClassroomChatMessage> PostChatMessageAsync(Guid classroomId, string userId, string body, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClassroomChatMessage>> GetChatMessagesAsync(Guid classroomId, string userId, DateTimeOffset? before = null, int take = 50, CancellationToken cancellationToken = default);
    Task<int> GetUnreadChatCountAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);
    Task MarkChatReadAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);

    Task PostAnnouncementAsync(Guid classroomId, string userId, string body, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClassroomBoardMessage>> GetBoardAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);
    Task SetPinnedAsync(Guid classroomId, string userId, Guid messageId, bool pinned, CancellationToken cancellationToken = default);
    Task DeleteMessageAsync(Guid classroomId, string userId, Guid messageId, CancellationToken cancellationToken = default);
}

public sealed class ClassroomConversation : IClassroomConversation
{
    private const int AnnouncementMaxLength = 4000;
    private const int ChatMessageMaxLength = 2000;

    private readonly GlosifyContext _context;
    private readonly IClassroomAccess _access;
    private readonly ClassroomQueries _queries;

    public ClassroomConversation(
        GlosifyContext context,
        IClassroomAccess access,
        ClassroomQueries queries)
    {
        _context = context;
        _access = access;
        _queries = queries;
    }

    public async Task<ClassroomChatMessage> PostChatMessageAsync(Guid classroomId, string userId, string body, CancellationToken cancellationToken = default)
    {
        await _access.RequireMemberAsync(classroomId, userId, cancellationToken);

        body = body.Trim();
        if (body.Length == 0)
        {
            throw new ArgumentException("Write a message before sending.");
        }

        if (body.Length > ChatMessageMaxLength)
        {
            throw new ArgumentException($"Chat messages are limited to {ChatMessageMaxLength} characters.");
        }

        var message = new ClassroomMessage
        {
            Id = Guid.NewGuid(),
            ClassroomId = classroomId,
            UserId = userId,
            Kind = ClassroomMessageKind.Chat,
            Body = body,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _context.ClassroomMessages.Add(message);
        await _context.SaveChangesAsync(cancellationToken);

        var authorName = await _context.Users
            .Where(u => u.Id == userId)
            .Select(u => u.UserName ?? u.Email)
            .FirstOrDefaultAsync(cancellationToken) ?? "Unknown";

        return new ClassroomChatMessage(message.Id, userId, authorName, message.Body, message.CreatedAt);
    }

    public async Task<IReadOnlyList<ClassroomChatMessage>> GetChatMessagesAsync(Guid classroomId, string userId, DateTimeOffset? before = null, int take = 50, CancellationToken cancellationToken = default)
    {
        await _access.RequireMemberAsync(classroomId, userId, cancellationToken);

        take = Math.Clamp(take, 1, 100);
        var query = _context.ClassroomMessages
            .AsNoTracking()
            .Where(m => m.ClassroomId == classroomId && m.Kind == ClassroomMessageKind.Chat && !m.IsDeleted);

        if (before.HasValue)
        {
            query = query.Where(m => m.CreatedAt < before.Value);
        }

        var page = await query
            .OrderByDescending(m => m.CreatedAt)
            .Take(take)
            .Join(_context.Users.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => new { m, AuthorName = u.UserName ?? u.Email ?? "Unknown" })
            .Select(x => new ClassroomChatMessage(x.m.Id, x.m.UserId, x.AuthorName, x.m.Body, x.m.CreatedAt))
            .ToListAsync(cancellationToken);

        // Fetched newest-first for paging; render oldest-first.
        page.Reverse();
        return page;
    }

    public async Task<int> GetUnreadChatCountAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default)
    {
        var membership = await _context.ClassroomMemberships
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.ClassroomId == classroomId && m.UserId == userId, cancellationToken);

        if (membership == null)
        {
            return 0;
        }

        return await _queries.UnreadChatCountAsync(membership, cancellationToken);
    }

    public async Task MarkChatReadAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default)
    {
        var membership = await _context.ClassroomMemberships
            .FirstOrDefaultAsync(m => m.ClassroomId == classroomId && m.UserId == userId, cancellationToken)
            ?? throw new ClassroomAccessDeniedException();

        membership.LastChatReadAt = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task PostAnnouncementAsync(Guid classroomId, string userId, string body, CancellationToken cancellationToken = default)
    {
        await _access.RequireTeacherAsync(classroomId, userId, cancellationToken);

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
        await _access.RequireMemberAsync(classroomId, userId, cancellationToken);
        return await _queries.BoardAsync(classroomId, cancellationToken);
    }

    public async Task SetPinnedAsync(Guid classroomId, string userId, Guid messageId, bool pinned, CancellationToken cancellationToken = default)
    {
        await _access.RequireTeacherAsync(classroomId, userId, cancellationToken);

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
        var membership = await _access.RequireMemberAsync(classroomId, userId, cancellationToken);

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
}
