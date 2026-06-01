using Glosify.Data;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Quizzes
{
    public class CollectionService : ICollectionService
    {
        private readonly GlosifyContext _context;

        public CollectionService(GlosifyContext context)
        {
            _context = context;
        }

        public async Task<IReadOnlyList<Collection>> GetCollectionsAsync(string userId, string language)
        {
            language = language.Trim();

            return await _context.Collections
                .Where(c => c.UserId == userId && c.Language == language)
                .OrderBy(c => c.ParentCollectionId.HasValue)
                .ThenBy(c => c.Name)
                .ToListAsync();
        }

        public async Task<Collection?> GetCollectionAsync(Guid id, string userId)
        {
            return await _context.Collections
                .Include(c => c.ChildCollections)
                .Include(c => c.Quizzes)
                .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);
        }

        public async Task<Collection> CreateCollectionAsync(
            string name,
            string language,
            string userId,
            Guid? parentCollectionId)
        {
            name = name.Trim();
            language = language.Trim();

            if (parentCollectionId.HasValue)
            {
                var parentExists = await _context.Collections.AnyAsync(c =>
                    c.Id == parentCollectionId.Value
                    && c.UserId == userId
                    && c.Language == language);

                if (!parentExists)
                {
                    throw new InvalidOperationException("Parent collection not found for this user and language.");
                }
            }

            var siblingNameExists = await _context.Collections.AnyAsync(c =>
                c.UserId == userId
                && c.Language == language
                && c.ParentCollectionId == parentCollectionId
                && c.Name == name);

            if (siblingNameExists)
            {
                throw new InvalidOperationException("A collection with this name already exists here.");
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
            await _context.SaveChangesAsync();

            return collection;
        }

        public async Task<bool> MoveCollectionAsync(Guid collectionId, Guid? parentCollectionId, string userId)
        {
            if (collectionId == parentCollectionId)
            {
                return false;
            }

            var collection = await _context.Collections
                .FirstOrDefaultAsync(c => c.Id == collectionId && c.UserId == userId);

            if (collection is null)
            {
                return false;
            }

            if (parentCollectionId.HasValue)
            {
                var parent = await _context.Collections
                    .FirstOrDefaultAsync(c => c.Id == parentCollectionId.Value && c.UserId == userId);

                if (parent is null || parent.Language != collection.Language)
                {
                    return false;
                }

                if (await IsDescendantAsync(parent.Id, collection.Id, userId))
                {
                    return false;
                }
            }

            var siblingNameExists = await _context.Collections.AnyAsync(c =>
                c.Id != collection.Id
                && c.UserId == userId
                && c.Language == collection.Language
                && c.ParentCollectionId == parentCollectionId
                && c.Name == collection.Name);

            if (siblingNameExists)
            {
                return false;
            }

            collection.ParentCollectionId = parentCollectionId;
            await _context.SaveChangesAsync();

            return true;
        }

        public async Task<bool> RenameCollectionAsync(Guid collectionId, string name, string userId)
        {
            name = name.Trim();

            var collection = await _context.Collections
                .FirstOrDefaultAsync(c => c.Id == collectionId && c.UserId == userId);

            if (collection is null)
            {
                return false;
            }

            var siblingNameExists = await _context.Collections.AnyAsync(c =>
                c.Id != collection.Id
                && c.UserId == userId
                && c.Language == collection.Language
                && c.ParentCollectionId == collection.ParentCollectionId
                && c.Name == name);

            if (siblingNameExists)
            {
                return false;
            }

            collection.Name = name;
            await _context.SaveChangesAsync();

            return true;
        }

        public async Task<bool> DeleteCollectionAsync(Guid collectionId, string userId)
        {
            var collection = await _context.Collections
                .FirstOrDefaultAsync(c => c.Id == collectionId && c.UserId == userId);

            if (collection is null)
            {
                return false;
            }

            var hasChildCollections = await _context.Collections
                .AnyAsync(c => c.ParentCollectionId == collectionId && c.UserId == userId);

            if (hasChildCollections)
            {
                return false;
            }

            await _context.Quizzes
                .Where(q => q.CollectionId == collectionId && q.UserId == userId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(q => q.CollectionId, (Guid?)null));

            _context.Collections.Remove(collection);
            await _context.SaveChangesAsync();

            return true;
        }

        public async Task<bool> DeleteCollectionTreeAsync(Guid collectionId, string userId)
        {
            var rootExists = await _context.Collections
                .AnyAsync(c => c.Id == collectionId && c.UserId == userId);

            if (!rootExists)
            {
                return false;
            }

            var collectionIds = await GetDescendantCollectionIdsAsync(collectionId, userId);

            var quizIds = await _context.Quizzes
                .Where(q => q.CollectionId.HasValue
                    && collectionIds.Contains(q.CollectionId.Value)
                    && q.UserId == userId)
                .Select(q => q.Id)
                .ToListAsync();

            if (quizIds.Count > 0)
            {
                await _context.Words
                    .Where(word => quizIds.Contains(word.QuizId))
                    .ExecuteDeleteAsync();

                await _context.Quizzes
                    .Where(q => quizIds.Contains(q.Id) && q.UserId == userId)
                    .ExecuteDeleteAsync();
            }

            var collections = await _context.Collections
                .Where(c => collectionIds.Contains(c.Id) && c.UserId == userId)
                .ToListAsync();

            var collectionDepths = GetCollectionDepths(collections, collectionId);

            foreach (var collection in collections.OrderByDescending(c => collectionDepths[c.Id]))
            {
                _context.Collections.Remove(collection);
            }

            await _context.SaveChangesAsync();

            return true;
        }

        public async Task<bool> MoveQuizToCollectionAsync(Guid quizId, Guid? collectionId, string userId)
        {
            var quiz = await _context.Quizzes
                .FirstOrDefaultAsync(q => q.Id == quizId && q.UserId == userId);

            if (quiz is null)
            {
                return false;
            }

            if (collectionId.HasValue)
            {
                var collectionExists = await _context.Collections.AnyAsync(c =>
                    c.Id == collectionId.Value
                    && c.UserId == userId
                    && c.Language == quiz.TargetLanguage);

                if (!collectionExists)
                {
                    return false;
                }
            }

            quiz.CollectionId = collectionId;
            await _context.SaveChangesAsync();

            return true;
        }

        private async Task<List<Guid>> GetDescendantCollectionIdsAsync(Guid rootCollectionId, string userId)
        {
            var collectionIds = new List<Guid> { rootCollectionId };
            var frontier = new List<Guid> { rootCollectionId };

            while (frontier.Count > 0)
            {
                var childIds = await _context.Collections
                    .Where(c => c.ParentCollectionId.HasValue
                        && frontier.Contains(c.ParentCollectionId.Value)
                        && c.UserId == userId)
                    .Select(c => c.Id)
                    .ToListAsync();

                frontier = childIds
                    .Where(id => !collectionIds.Contains(id))
                    .ToList();

                collectionIds.AddRange(frontier);
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

        private async Task<bool> IsDescendantAsync(Guid possibleDescendantId, Guid ancestorId, string userId)
        {
            var currentId = possibleDescendantId;

            while (true)
            {
                var parentId = await _context.Collections
                    .Where(c => c.Id == currentId && c.UserId == userId)
                    .Select(c => c.ParentCollectionId)
                    .FirstOrDefaultAsync();

                if (!parentId.HasValue)
                {
                    return false;
                }

                if (parentId.Value == ancestorId)
                {
                    return true;
                }

                currentId = parentId.Value;
            }
        }
    }
}
