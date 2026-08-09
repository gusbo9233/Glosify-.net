using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Models.Library;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Classrooms;

/// <summary>
/// The quizzes and books a teacher has shared into a classroom, and the checks that let a
/// member reach one.
/// </summary>
public interface IClassroomLibrary
{
    Task ShareQuizAsync(Guid classroomId, string userId, Guid quizId, CancellationToken cancellationToken = default);
    Task ShareBookAsync(Guid classroomId, string userId, Guid bookDocumentId, CancellationToken cancellationToken = default);
    Task UnshareContentAsync(Guid classroomId, string userId, Guid contentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClassroomContentItem>> GetContentAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default);
    Task<Quiz> RequireSharedQuizAsync(Guid classroomId, Guid quizId, string userId, CancellationToken cancellationToken = default);
    Task<BookDocument> RequireSharedBookAsync(Guid classroomId, Guid bookDocumentId, string userId, CancellationToken cancellationToken = default);
}

public sealed class ClassroomLibrary : IClassroomLibrary
{
    private readonly GlosifyContext _context;
    private readonly IClassroomAccess _access;
    private readonly ClassroomQueries _queries;

    public ClassroomLibrary(
        GlosifyContext context,
        IClassroomAccess access,
        ClassroomQueries queries)
    {
        _context = context;
        _access = access;
        _queries = queries;
    }

    public async Task ShareQuizAsync(Guid classroomId, string userId, Guid quizId, CancellationToken cancellationToken = default)
    {
        await _access.RequireTeacherAsync(classroomId, userId, cancellationToken);

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
        await _access.RequireTeacherAsync(classroomId, userId, cancellationToken);

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
        await _access.RequireTeacherAsync(classroomId, userId, cancellationToken);

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
        await _access.RequireMemberAsync(classroomId, userId, cancellationToken);
        return await _queries.ContentAsync(classroomId, cancellationToken);
    }

    public async Task<Quiz> RequireSharedQuizAsync(Guid classroomId, Guid quizId, string userId, CancellationToken cancellationToken = default)
    {
        await _access.RequireMemberAsync(classroomId, userId, cancellationToken);

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
        await _access.RequireMemberAsync(classroomId, userId, cancellationToken);

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
}
