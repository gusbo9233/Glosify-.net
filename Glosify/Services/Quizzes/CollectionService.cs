using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Glosify.Services.Anki;

namespace Glosify.Services.Quizzes;

public class CollectionService : ICollectionService
{
    private readonly GlosifyContext _context;
    private readonly IAnkiCollectionService _ankiCollections;

    public CollectionService(GlosifyContext context, IAnkiCollectionService ankiCollections)
    {
        _context = context;
        _ankiCollections = ankiCollections;
    }

    public async Task<IReadOnlyList<Collection>> GetCollectionsAsync(string userId, string language, CancellationToken cancellationToken = default)
    {
        language = language.Trim();

        return await _context.Collections
            .AsNoTracking()
            .Where(c => c.UserId == userId && c.Language == language)
            .OrderBy(c => c.ParentCollectionId.HasValue)
            .ThenBy(c => c.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<Collection?> GetCollectionAsync(Guid id, string userId, CancellationToken cancellationToken = default)
    {
        // AsSplitQuery because these are two sibling collection navigations: a single
        // query returns children x quizzes rows with the parent's columns repeated
        // across every one of them, which grows multiplicatively with the data.
        return await _context.Collections
            .AsNoTracking()
            .Include(c => c.ChildCollections)
            .Include(c => c.Quizzes)
            .AsSplitQuery()
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, cancellationToken);
    }

    public async Task<Collection> CreateCollectionAsync(
        string name,
        string language,
        string userId,
        Guid? parentCollectionId,
        CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        language = language.Trim();

        if (parentCollectionId.HasValue)
        {
            var parentExists = await _context.Collections.AnyAsync(c =>
                c.Id == parentCollectionId.Value
                && c.UserId == userId
                && c.Language == language, cancellationToken);

            if (!parentExists)
            {
                throw new CollectionParentNotFoundException();
            }
        }

        var siblingNameExists = await _context.Collections.AnyAsync(c =>
            c.UserId == userId
            && c.Language == language
            && c.ParentCollectionId == parentCollectionId
            && c.Name == name, cancellationToken);

        if (siblingNameExists)
        {
            throw new CollectionNameConflictException();
        }

        var collection = new Collection
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name,
            Language = language,
            ParentCollectionId = parentCollectionId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _context.Collections.Add(collection);
        await _context.SaveChangesAsync(cancellationToken);

        return collection;
    }

    public async Task<bool> MoveCollectionAsync(Guid collectionId, Guid? parentCollectionId, string userId, CancellationToken cancellationToken = default)
    {
        if (collectionId == parentCollectionId)
        {
            return false;
        }

        var collection = await _context.Collections
            .FirstOrDefaultAsync(c => c.Id == collectionId && c.UserId == userId, cancellationToken);

        if (collection is null)
        {
            return false;
        }

        if (parentCollectionId.HasValue)
        {
            var parent = await _context.Collections
                .FirstOrDefaultAsync(c => c.Id == parentCollectionId.Value && c.UserId == userId, cancellationToken);

            if (parent is null || parent.Language != collection.Language)
            {
                return false;
            }

            if (await IsDescendantAsync(parent.Id, collection.Id, userId, cancellationToken))
            {
                return false;
            }
        }

        var siblingNameExists = await _context.Collections.AnyAsync(c =>
            c.Id != collection.Id
            && c.UserId == userId
            && c.Language == collection.Language
            && c.ParentCollectionId == parentCollectionId
            && c.Name == collection.Name, cancellationToken);

        if (siblingNameExists)
        {
            return false;
        }

        collection.ParentCollectionId = parentCollectionId;
        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<bool> RenameCollectionAsync(Guid collectionId, string name, string userId, CancellationToken cancellationToken = default)
    {
        name = name.Trim();

        var collection = await _context.Collections
            .FirstOrDefaultAsync(c => c.Id == collectionId && c.UserId == userId, cancellationToken);

        if (collection is null)
        {
            return false;
        }

        var siblingNameExists = await _context.Collections.AnyAsync(c =>
            c.Id != collection.Id
            && c.UserId == userId
            && c.Language == collection.Language
            && c.ParentCollectionId == collection.ParentCollectionId
            && c.Name == name, cancellationToken);

        if (siblingNameExists)
        {
            return false;
        }

        collection.Name = name;
        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<bool> DeleteCollectionAsync(Guid collectionId, string userId, CancellationToken cancellationToken = default)
    {
        var collection = await _context.Collections
            .FirstOrDefaultAsync(c => c.Id == collectionId && c.UserId == userId, cancellationToken);

        if (collection is null)
        {
            return false;
        }

        var hasChildCollections = await _context.Collections
            .AnyAsync(c => c.ParentCollectionId == collectionId && c.UserId == userId, cancellationToken);

        if (hasChildCollections)
        {
            return false;
        }

        var quizzes = await _context.Quizzes
            .Where(q => q.CollectionId == collectionId && q.UserId == userId)
            .ToListAsync(cancellationToken);
        foreach (var quiz in quizzes) quiz.CollectionId = null;

        _context.Collections.Remove(collection);
        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<bool> DeleteCollectionTreeAsync(Guid collectionId, string userId, CancellationToken cancellationToken = default)
    {
        var rootExists = await _context.Collections
            .AnyAsync(c => c.Id == collectionId && c.UserId == userId, cancellationToken);

        if (!rootExists)
        {
            return false;
        }

        var collectionIds = await GetDescendantCollectionIdsAsync(collectionId, userId, cancellationToken);

        var quizzes = await _context.Quizzes
            .Where(q => q.CollectionId.HasValue
                && collectionIds.Contains(q.CollectionId.Value)
                && q.UserId == userId)
            .ToListAsync(cancellationToken);

        if (quizzes.Count > 0)
        {
            var quizIds = quizzes.Select(q => q.Id).ToList();
            foreach (var quizId in quizIds)
                await _ankiCollections.RetireQuizAsync(quizId, cancellationToken);

            var words = await _context.Words
                .Where(word => quizIds.Contains(word.QuizId))
                .ToListAsync(cancellationToken);

            // Saved assistant chats reference quizzes through no-action foreign
            // keys; clear those references first or deleting the quizzes fails.
            var assistantThreads = await _context.AssistantThreads
                .Where(thread => thread.ContextQuizId.HasValue && quizIds.Contains(thread.ContextQuizId.Value))
                .ToListAsync(cancellationToken);
            var assistantMessages = await _context.AssistantMessages
                .Where(message => message.ContextQuizId.HasValue && quizIds.Contains(message.ContextQuizId.Value))
                .ToListAsync(cancellationToken);

            foreach (var thread in assistantThreads)
            {
                thread.ContextQuizId = null;
            }

            foreach (var message in assistantMessages)
            {
                message.ContextQuizId = null;
            }

            _context.Words.RemoveRange(words);
            _context.Quizzes.RemoveRange(quizzes);
        }

        var collections = await _context.Collections
            .Where(c => collectionIds.Contains(c.Id) && c.UserId == userId)
            .ToListAsync(cancellationToken);

        var collectionDepths = GetCollectionDepths(collections, collectionId);

        foreach (var collection in collections.OrderByDescending(c => collectionDepths[c.Id]))
        {
            _context.Collections.Remove(collection);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> MoveQuizToCollectionAsync(Guid quizId, Guid? collectionId, string userId, CancellationToken cancellationToken = default)
    {
        var quiz = await _context.Quizzes
            .FirstOrDefaultAsync(q => q.Id == quizId && q.UserId == userId, cancellationToken);

        if (quiz is null)
        {
            return false;
        }

        if (collectionId.HasValue)
        {
            var collectionExists = await _context.Collections.AnyAsync(c =>
                c.Id == collectionId.Value
                && c.UserId == userId
                && c.Language == quiz.TargetLanguage, cancellationToken);

            if (!collectionExists)
            {
                return false;
            }
        }

        quiz.CollectionId = collectionId;
        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }

    private async Task<List<Guid>> GetDescendantCollectionIdsAsync(Guid rootCollectionId, string userId, CancellationToken cancellationToken)
    {
        var links = await _context.Collections
            .AsNoTracking()
            .Where(c => c.UserId == userId && c.ParentCollectionId.HasValue)
            .Select(c => new { c.Id, ParentId = c.ParentCollectionId!.Value })
            .ToListAsync(cancellationToken);

        var childrenByParent = links
            .GroupBy(link => link.ParentId)
            .ToDictionary(group => group.Key, group => group.Select(link => link.Id).ToList());

        var collectionIds = new List<Guid> { rootCollectionId };
        var seen = new HashSet<Guid> { rootCollectionId };
        for (var i = 0; i < collectionIds.Count; i++)
        {
            if (!childrenByParent.TryGetValue(collectionIds[i], out var childIds))
            {
                continue;
            }

            foreach (var childId in childIds)
            {
                if (seen.Add(childId))
                {
                    collectionIds.Add(childId);
                }
            }
        }

        return collectionIds;
    }

    private static Dictionary<Guid, int> GetCollectionDepths(IReadOnlyCollection<Collection> collections, Guid rootCollectionId)
    {
        var depths = new Dictionary<Guid, int>
        {
            [rootCollectionId] = 0
        };

        while (depths.Count < collections.Count)
        {
            var addedDepth = false;

            foreach (var collection in collections)
            {
                if (depths.ContainsKey(collection.Id))
                {
                    continue;
                }

                if (collection.ParentCollectionId.HasValue
                    && depths.TryGetValue(collection.ParentCollectionId.Value, out var parentDepth))
                {
                    depths[collection.Id] = parentDepth + 1;
                    addedDepth = true;
                }
            }

            if (!addedDepth)
            {
                foreach (var collection in collections)
                {
                    depths.TryAdd(collection.Id, 0);
                }
            }
        }

        return depths;
    }

    private async Task<bool> IsDescendantAsync(Guid possibleDescendantId, Guid ancestorId, string userId, CancellationToken cancellationToken)
    {
        var parentsById = await _context.Collections
            .AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => new { c.Id, c.ParentCollectionId })
            .ToDictionaryAsync(c => c.Id, c => c.ParentCollectionId, cancellationToken);

        var visited = new HashSet<Guid>();
        var currentId = possibleDescendantId;

        while (visited.Add(currentId)
            && parentsById.TryGetValue(currentId, out var parentId)
            && parentId.HasValue)
        {
            if (parentId.Value == ancestorId)
            {
                return true;
            }

            currentId = parentId.Value;
        }

        return false;
    }

}
