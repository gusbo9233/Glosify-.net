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

        public async Task<bool> SetCollectionPublicAsync(Guid collectionId, string userId, bool isPublic)
        {
            var collection = await _context.Collections
                .FirstOrDefaultAsync(c => c.Id == collectionId && c.UserId == userId);

            if (collection is null)
            {
                return false;
            }

            collection.IsPublic = isPublic;
            await _context.SaveChangesAsync();

            return true;
        }

        public async Task<IReadOnlyList<Collection>> GetPublicCollectionRootsAsync(string language)
        {
            language = language.Trim();

            var collections = await _context.Collections
                .AsNoTracking()
                .Where(c => c.Language == language)
                .OrderBy(c => c.Name)
                .ToListAsync();

            var byId = collections.ToDictionary(c => c.Id);
            return collections
                .Where(c => c.IsPublic && !HasPublicAncestor(c, byId))
                .ToList();
        }

        public async Task<Collection?> GetPublicCollectionTreeAsync(Guid collectionId)
        {
            var root = await _context.Collections
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == collectionId);

            if (root is null || !await IsCollectionPubliclyReadableAsync(root))
            {
                return null;
            }

            var collectionIds = await GetDescendantCollectionIdsAsync(collectionId, root.UserId);
            var collections = await _context.Collections
                .AsNoTracking()
                .Where(c => collectionIds.Contains(c.Id))
                .OrderBy(c => c.Name)
                .ToListAsync();
            var quizzes = await _context.Quizzes
                .AsNoTracking()
                .Where(q => q.CollectionId.HasValue && collectionIds.Contains(q.CollectionId.Value))
                .OrderBy(q => q.Name)
                .ToListAsync();

            return BuildCollectionTree(collectionId, collections, quizzes);
        }

        public async Task<Collection?> CopyPublicCollectionAsync(Guid collectionId, string userId)
        {
            var sourceRoot = await _context.Collections
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == collectionId);

            if (sourceRoot is null || !await IsCollectionPubliclyReadableAsync(sourceRoot))
            {
                return null;
            }

            var sourceCollectionIds = await GetDescendantCollectionIdsAsync(collectionId, sourceRoot.UserId);
            var sourceCollections = await _context.Collections
                .AsNoTracking()
                .Where(c => sourceCollectionIds.Contains(c.Id))
                .ToListAsync();
            var sourceQuizzes = await _context.Quizzes
                .AsNoTracking()
                .Where(q => q.CollectionId.HasValue && sourceCollectionIds.Contains(q.CollectionId.Value))
                .ToListAsync();
            var sourceQuizIds = sourceQuizzes.Select(q => q.Id).ToList();
            var sourceWords = await _context.Words
                .AsNoTracking()
                .Where(word => sourceQuizIds.Contains(word.QuizId))
                .ToListAsync();
            var sourceSentences = await _context.QuizSentences
                .AsNoTracking()
                .Where(sentence => sourceQuizIds.Contains(sentence.QuizId))
                .ToListAsync();

            var collectionDepths = GetCollectionDepths(sourceCollections, collectionId);
            var collectionIdMap = new Dictionary<Guid, Guid>();

            foreach (var source in sourceCollections.OrderBy(c => collectionDepths[c.Id]))
            {
                var copyId = Guid.NewGuid();
                collectionIdMap[source.Id] = copyId;

                _context.Collections.Add(new Collection
                {
                    Id = copyId,
                    UserId = userId,
                    Name = source.Name,
                    Language = source.Language,
                    ParentCollectionId = source.ParentCollectionId.HasValue
                        && collectionIdMap.TryGetValue(source.ParentCollectionId.Value, out var copiedParentId)
                            ? copiedParentId
                            : null,
                    CreatedAt = DateTimeOffset.UtcNow,
                    IsPublic = false,
                    OriginalCollectionId = source.Id
                });
            }

            var quizIdMap = new Dictionary<Guid, Guid>();
            foreach (var source in sourceQuizzes)
            {
                var copiedQuizId = Guid.NewGuid();
                quizIdMap[source.Id] = copiedQuizId;

                _context.Quizzes.Add(new Quiz
                {
                    Id = copiedQuizId,
                    Name = source.Name,
                    UserId = userId,
                    CollectionId = source.CollectionId.HasValue ? collectionIdMap[source.CollectionId.Value] : null,
                    CreatedAt = DateTimeOffset.UtcNow,
                    IsSongQuiz = source.IsSongQuiz,
                    ProcessingStatus = source.ProcessingStatus,
                    ProcessingMessage = source.ProcessingMessage,
                    SourceLanguage = source.SourceLanguage,
                    TargetLanguage = source.TargetLanguage,
                    Language = source.Language,
                    AnkiTrackingEnabled = source.AnkiTrackingEnabled,
                    AnkiTrackWordsForward = source.AnkiTrackWordsForward,
                    AnkiTrackWordsReverse = source.AnkiTrackWordsReverse,
                    AnkiTrackSentencesForward = source.AnkiTrackSentencesForward,
                    AnkiTrackSentencesReverse = source.AnkiTrackSentencesReverse,
                    IsPublic = false,
                    OriginalQuizId = source.Id
                });
            }

            _context.Words.AddRange(sourceWords.Select(word => new Word
            {
                Id = Guid.NewGuid().ToString("N"),
                QuizId = quizIdMap[word.QuizId],
                Lemma = word.Lemma,
                Translation = word.Translation
            }));
            _context.QuizSentences.AddRange(sourceSentences.Select(sentence => new QuizSentence
            {
                Id = Guid.NewGuid(),
                QuizId = quizIdMap[sentence.QuizId],
                Text = sentence.Text,
                Translation = sentence.Translation,
                CreatedAt = DateTimeOffset.UtcNow
            }));

            await _context.SaveChangesAsync();
            var copiedRootId = collectionIdMap[collectionId];
            return await GetCollectionAsync(copiedRootId, userId);
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

        private async Task<bool> IsCollectionPubliclyReadableAsync(Collection collection)
        {
            var current = collection;

            while (true)
            {
                if (current.IsPublic)
                {
                    return true;
                }

                if (!current.ParentCollectionId.HasValue)
                {
                    return false;
                }

                var parent = await _context.Collections
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == current.ParentCollectionId.Value);

                if (parent is null)
                {
                    return false;
                }

                current = parent;
            }
        }

        private static bool HasPublicAncestor(Collection collection, IReadOnlyDictionary<Guid, Collection> byId)
        {
            var current = collection;

            while (current.ParentCollectionId.HasValue
                && byId.TryGetValue(current.ParentCollectionId.Value, out var parent))
            {
                if (parent.IsPublic)
                {
                    return true;
                }

                current = parent;
            }

            return false;
        }

        private static Collection? BuildCollectionTree(
            Guid rootId,
            IReadOnlyList<Collection> collections,
            IReadOnlyList<Quiz> quizzes)
        {
            var byId = collections.ToDictionary(c => c.Id);
            if (!byId.TryGetValue(rootId, out var root))
            {
                return null;
            }

            foreach (var collection in collections)
            {
                collection.ChildCollections.Clear();
                collection.Quizzes.Clear();
            }

            foreach (var collection in collections.Where(c => c.ParentCollectionId.HasValue))
            {
                if (byId.TryGetValue(collection.ParentCollectionId!.Value, out var parent))
                {
                    parent.ChildCollections.Add(collection);
                    collection.ParentCollection = parent;
                }
            }

            foreach (var quiz in quizzes)
            {
                if (quiz.CollectionId.HasValue && byId.TryGetValue(quiz.CollectionId.Value, out var collection))
                {
                    collection.Quizzes.Add(quiz);
                    quiz.Collection = collection;
                }
            }

            return root;
        }
    }
}
