using Glosify.Data;
using Microsoft.EntityFrameworkCore;
using Glosify.Services.CustomQuizzes;

namespace Glosify.Services.Quizzes
{
    public class CollectionService : ICollectionService
    {
        private readonly GlosifyContext _context;
        private readonly CollectionVisibility _collectionVisibility;

        public CollectionService(GlosifyContext context)
        {
            _context = context;
            _collectionVisibility = new CollectionVisibility(context);
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
            return await _context.Collections
                .AsNoTracking()
                .Include(c => c.ChildCollections)
                .Include(c => c.Quizzes)
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
                    throw new InvalidOperationException("Parent collection not found for this user and language.");
                }
            }

            var siblingNameExists = await _context.Collections.AnyAsync(c =>
                c.UserId == userId
                && c.Language == language
                && c.ParentCollectionId == parentCollectionId
                && c.Name == name, cancellationToken);

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

            await _context.Quizzes
                .Where(q => q.CollectionId == collectionId && q.UserId == userId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(q => q.CollectionId, (Guid?)null), cancellationToken);

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

        public async Task<bool> SetCollectionPublicAsync(Guid collectionId, string userId, bool isPublic, CancellationToken cancellationToken = default)
        {
            var collection = await _context.Collections
                .FirstOrDefaultAsync(c => c.Id == collectionId && c.UserId == userId, cancellationToken);

            if (collection is null)
            {
                return false;
            }

            collection.IsPublic = isPublic;
            await _context.SaveChangesAsync(cancellationToken);

            return true;
        }

        public async Task<IReadOnlyList<Collection>> GetPublicCollectionRootsAsync(string language, CancellationToken cancellationToken = default)
        {
            language = language.Trim();

            var collections = await _context.Collections
                .AsNoTracking()
                .Where(c => c.Language == language)
                .OrderBy(c => c.Name)
                .ToListAsync(cancellationToken);

            var byId = collections.ToDictionary(c => c.Id);
            return collections
                .Where(c => c.IsPublic && !HasPublicAncestor(c, byId))
                .ToList();
        }

        public async Task<IReadOnlyList<PublicCollectionSummary>> GetPublicCollectionSummariesAsync(string language, CancellationToken cancellationToken = default)
        {
            language = language.Trim();

            var collections = await _context.Collections
                .AsNoTracking()
                .Where(c => c.Language == language)
                .OrderBy(c => c.Name)
                .ToListAsync(cancellationToken);

            var byId = collections.ToDictionary(c => c.Id);
            var roots = collections
                .Where(c => c.IsPublic && !HasPublicAncestor(c, byId))
                .ToList();

            if (roots.Count == 0)
            {
                return [];
            }

            var childrenByParent = collections
                .Where(c => c.ParentCollectionId.HasValue)
                .GroupBy(c => c.ParentCollectionId!.Value)
                .ToDictionary(group => group.Key, group => group.Select(c => c.Id).ToList());

            var collectionIds = collections.Select(c => c.Id).ToList();
            var quizCounts = await _context.Quizzes
                .AsNoTracking()
                .Where(q => q.CollectionId.HasValue && collectionIds.Contains(q.CollectionId.Value))
                .GroupBy(q => q.CollectionId!.Value)
                .Select(group => new { CollectionId = group.Key, Count = group.Count() })
                .ToDictionaryAsync(group => group.CollectionId, group => group.Count, cancellationToken);

            var summaries = new List<PublicCollectionSummary>(roots.Count);
            foreach (var root in roots)
            {
                var treeIds = CollectSubtreeIds(root.Id, childrenByParent);
                summaries.Add(new PublicCollectionSummary(
                    root,
                    treeIds.Count - 1,
                    treeIds.Sum(id => quizCounts.GetValueOrDefault(id))));
            }

            return summaries;
        }

        private static List<Guid> CollectSubtreeIds(Guid rootId, IReadOnlyDictionary<Guid, List<Guid>> childrenByParent)
        {
            var subtreeIds = new List<Guid> { rootId };
            var seen = new HashSet<Guid> { rootId };
            for (var i = 0; i < subtreeIds.Count; i++)
            {
                if (!childrenByParent.TryGetValue(subtreeIds[i], out var childIds))
                {
                    continue;
                }

                foreach (var childId in childIds)
                {
                    if (seen.Add(childId))
                    {
                        subtreeIds.Add(childId);
                    }
                }
            }

            return subtreeIds;
        }

        public async Task<Collection?> GetPublicCollectionTreeAsync(Guid collectionId, CancellationToken cancellationToken = default)
        {
            var root = await _context.Collections
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == collectionId, cancellationToken);

            if (root is null || !await _collectionVisibility.IsCollectionPubliclyReadableAsync(root.Id))
            {
                return null;
            }

            var collectionIds = await GetDescendantCollectionIdsAsync(collectionId, root.UserId, cancellationToken);
            var collections = await _context.Collections
                .AsNoTracking()
                .Where(c => collectionIds.Contains(c.Id))
                .OrderBy(c => c.Name)
                .ToListAsync(cancellationToken);
            var quizzes = await _context.Quizzes
                .AsNoTracking()
                .Where(q => q.CollectionId.HasValue && collectionIds.Contains(q.CollectionId.Value))
                .OrderBy(q => q.Name)
                .ToListAsync(cancellationToken);

            return BuildCollectionTree(collectionId, collections, quizzes);
        }

        public async Task<Collection?> CopyPublicCollectionAsync(Guid collectionId, string userId, CancellationToken cancellationToken = default)
        {
            var sourceRoot = await _context.Collections
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == collectionId, cancellationToken);

            if (sourceRoot is null || !await _collectionVisibility.IsCollectionPubliclyReadableAsync(sourceRoot.Id))
            {
                return null;
            }

            var sourceCollectionIds = await GetDescendantCollectionIdsAsync(collectionId, sourceRoot.UserId, cancellationToken);
            var sourceCollections = await _context.Collections
                .AsNoTracking()
                .Where(c => sourceCollectionIds.Contains(c.Id))
                .ToListAsync(cancellationToken);
            var sourceQuizzes = await _context.Quizzes
                .AsNoTracking()
                .Where(q => q.CollectionId.HasValue && sourceCollectionIds.Contains(q.CollectionId.Value))
                .ToListAsync(cancellationToken);
            var sourceQuizIds = sourceQuizzes.Select(q => q.Id).ToList();
            var sourceWords = await _context.Words
                .AsNoTracking()
                .Where(word => sourceQuizIds.Contains(word.QuizId))
                .ToListAsync(cancellationToken);
            var sourceSentences = await _context.QuizSentences
                .AsNoTracking()
                .Where(sentence => sourceQuizIds.Contains(sentence.QuizId))
                .ToListAsync(cancellationToken);

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

            var wordMapsByQuiz = sourceQuizIds.ToDictionary(id => id, _ => new Dictionary<string, string>(StringComparer.Ordinal));
            foreach (var word in sourceWords)
            {
                var copiedWordId = Guid.NewGuid().ToString("N");
                wordMapsByQuiz[word.QuizId][word.Id] = copiedWordId;
                _context.Words.Add(new Word
                {
                    Id = copiedWordId,
                    QuizId = quizIdMap[word.QuizId],
                    Lemma = word.Lemma,
                    Translation = word.Translation,
                    CreatedAt = word.CreatedAt
                });
            }
            _context.QuizSentences.AddRange(sourceSentences.Select(sentence => new QuizSentence
            {
                Id = Guid.NewGuid(),
                QuizId = quizIdMap[sentence.QuizId],
                Text = sentence.Text,
                Translation = sentence.Translation,
                CreatedAt = sentence.CreatedAt
            }));

            var customQuizService = new CustomQuizService(_context);
            foreach (var sourceQuizId in sourceQuizIds)
            {
                await customQuizService.CloneForCopiedQuizAsync(sourceQuizId, quizIdMap[sourceQuizId], wordMapsByQuiz[sourceQuizId], cancellationToken);
            }

            await _context.SaveChangesAsync(cancellationToken);
            var copiedRootId = collectionIdMap[collectionId];
            return await GetCollectionAsync(copiedRootId, userId, cancellationToken);
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
