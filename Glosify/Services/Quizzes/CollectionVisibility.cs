using Glosify.Data;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Quizzes;

/// <summary>
/// Shared answer to "is this collection publicly readable?": a collection is readable
/// when it or any ancestor is public. Loads the whole per-language collection graph in
/// one projected query and walks it in memory instead of issuing one query per
/// ancestor/tree level.
/// </summary>
internal sealed class CollectionVisibility
{
    private readonly GlosifyContext _context;

    public CollectionVisibility(GlosifyContext context)
    {
        _context = context;
    }

    private sealed record Node(Guid Id, Guid? ParentCollectionId, bool IsPublic);

    public async Task<bool> IsCollectionPubliclyReadableAsync(Guid collectionId, CancellationToken cancellationToken = default)
    {
        var target = await _context.Collections
            .AsNoTracking()
            .Where(c => c.Id == collectionId)
            .Select(c => new { c.Language, c.IsPublic })
            .FirstOrDefaultAsync(cancellationToken);

        if (target is null)
        {
            return false;
        }

        if (target.IsPublic)
        {
            return true;
        }

        var publicTreeIds = await GetPublicCollectionTreeIdsAsync(target.Language, cancellationToken);
        return publicTreeIds.Contains(collectionId);
    }

    /// <summary>
    /// Ids of every collection in the language that is public or sits below a public
    /// collection (i.e. the union of all public subtrees).
    /// </summary>
    public async Task<HashSet<Guid>> GetPublicCollectionTreeIdsAsync(string language, CancellationToken cancellationToken = default)
    {
        var nodes = await _context.Collections
            .AsNoTracking()
            .Where(c => c.Language == language)
            .Select(c => new Node(c.Id, c.ParentCollectionId, c.IsPublic))
            .ToListAsync(cancellationToken);

        var childrenByParent = nodes
            .Where(node => node.ParentCollectionId.HasValue)
            .GroupBy(node => node.ParentCollectionId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(node => node.Id).ToList());

        var result = new HashSet<Guid>();
        var frontier = nodes
            .Where(node => node.IsPublic)
            .Select(node => node.Id)
            .ToList();

        while (frontier.Count > 0)
        {
            var current = frontier[^1];
            frontier.RemoveAt(frontier.Count - 1);

            if (!result.Add(current))
            {
                continue;
            }

            if (childrenByParent.TryGetValue(current, out var childIds))
            {
                frontier.AddRange(childIds);
            }
        }

        return result;
    }
}
