using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class MoveCollectionTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "move_collection",
        "Propose moving a collection under another collection. Omit parent_collection_id to move it to the library root. The change is queued until the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["collection_id"] = StringProp("Id of the collection to move. Use list_collections to find it."),
            ["parent_collection_id"] = StringProp("Optional destination parent collection id. Omit to move the collection to the library root."),
        }, required: ["collection_id"]));

    public AgentToolDeclaration Declaration => DeclarationValue;

    private readonly GlosifyContext _context;

    public MoveCollectionTool(GlosifyContext context) => _context = context;

    public async Task<object> ExecuteAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        var collectionIdText = GetString(args, "collection_id");
        var destination = GetNullableGuidString(args, "parent_collection_id");
        if (!Guid.TryParse(collectionIdText, out var collectionId))
        {
            return new { error = "collection_id must be a valid id. Use list_collections to find collection ids." };
        }
        if (destination.Invalid)
        {
            return new { error = "parent_collection_id must be a valid id." };
        }
        if (destination.Value == collectionId)
        {
            return new { error = "A collection cannot be moved inside itself." };
        }

        var collection = await _context.Collections
            .FirstOrDefaultAsync(c => c.Id == collectionId && c.UserId == context.UserId, cancellationToken);
        if (collection == null)
        {
            return new { error = $"Collection {collectionId} was not found." };
        }

        Collection? parent = null;
        if (destination.Value.HasValue)
        {
            parent = await _context.Collections.FirstOrDefaultAsync(
                c => c.Id == destination.Value.Value
                    && c.UserId == context.UserId
                    && c.Language == collection.Language,
                cancellationToken);
            if (parent == null)
            {
                return new { error = "The destination collection was not found for this language." };
            }

            var parentMap = await _context.Collections
                .Where(c => c.UserId == context.UserId && c.Language == collection.Language)
                .ToDictionaryAsync(c => c.Id, c => c.ParentCollectionId, cancellationToken);
            var ancestorId = parent.Id;
            var visited = new HashSet<Guid>();
            while (true)
            {
                if (ancestorId == collection.Id)
                {
                    return new { error = "A collection cannot be moved inside one of its descendants." };
                }
                if (!visited.Add(ancestorId))
                {
                    return new { error = "The collection hierarchy contains a cycle and cannot be changed safely." };
                }
                if (!parentMap.TryGetValue(ancestorId, out var nextAncestor) || !nextAncestor.HasValue)
                {
                    break;
                }
                ancestorId = nextAncestor.Value;
            }
        }

        if (collection.ParentCollectionId == destination.Value)
        {
            return new { error = "The collection is already in that location." };
        }

        var duplicateExists = await _context.Collections.AnyAsync(c =>
            c.Id != collection.Id
            && c.UserId == context.UserId
            && c.Language == collection.Language
            && c.ParentCollectionId == destination.Value
            && c.Name == collection.Name,
            cancellationToken);
        if (duplicateExists)
        {
            return new { error = "A collection with that name already exists in the destination." };
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.MoveCollection,
            collection_id = collection.Id,
            collection_name = collection.Name,
            parent_collection_id = destination.Value,
            parent_collection_name = parent?.Name,
        }, JsonOptions);
        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.MoveCollection, payload));
        return new { queued = true, kind = PendingChangeKinds.MoveCollection, collection_id = collection.Id };
    }
}
