using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class RenameCollectionTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "rename_collection",
        "Propose renaming one of the user's collections. The change is queued until the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["collection_id"] = StringProp("Id of the collection to rename. Use list_collections to find it."),
            ["name"] = StringProp("New collection name."),
        }, required: ["collection_id", "name"]));

    public AgentToolDeclaration Declaration => DeclarationValue;

    private readonly GlosifyContext _context;

    public RenameCollectionTool(GlosifyContext context) => _context = context;

    public async Task<object> ExecuteAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        var collectionIdText = GetString(args, "collection_id");
        var name = GetString(args, "name")?.Trim();
        if (!Guid.TryParse(collectionIdText, out var collectionId))
        {
            return new { error = "collection_id must be a valid id. Use list_collections to find collection ids." };
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            return new { error = "name is required." };
        }

        var collection = await _context.Collections
            .FirstOrDefaultAsync(c => c.Id == collectionId && c.UserId == context.UserId, cancellationToken);
        if (collection == null)
        {
            return new { error = $"Collection {collectionId} was not found." };
        }
        if (string.Equals(collection.Name, name, StringComparison.Ordinal))
        {
            return new { error = "The collection already has that name." };
        }

        var duplicateExists = await _context.Collections.AnyAsync(c =>
            c.Id != collection.Id
            && c.UserId == context.UserId
            && c.Language == collection.Language
            && c.ParentCollectionId == collection.ParentCollectionId
            && c.Name == name,
            cancellationToken);
        if (duplicateExists)
        {
            return new { error = "A collection with that name already exists in the same location." };
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.RenameCollection,
            collection_id = collection.Id,
            original_name = collection.Name,
            name,
        }, JsonOptions);
        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.RenameCollection, payload));
        return new { queued = true, kind = PendingChangeKinds.RenameCollection, collection_id = collection.Id };
    }
}
