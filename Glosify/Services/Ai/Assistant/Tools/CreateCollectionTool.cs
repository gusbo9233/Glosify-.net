using System.Text.Json;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class CreateCollectionTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "create_collection",
        "Propose creating a collection. The change is queued; it is only saved when the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["name"] = StringProp("Collection name."),
            ["language"] = StringProp("Collection language. Defaults to the current app language when available."),
            ["parent_collection_id"] = StringProp("Optional id of the parent collection."),
        }, required: ["name"]));

    public AgentToolDeclaration Declaration => DeclarationValue;

    public Task<object> ExecuteAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(QueueCreateCollection(args, context));

    private static object QueueCreateCollection(JsonElement args, AgentToolContext context)
    {
        var name = GetString(args, "name");
        var language = FirstNonBlank(GetString(args, "language"), context.CurrentLanguage);
        var parentCollectionId = GetNullableGuidString(args, "parent_collection_id");

        if (string.IsNullOrWhiteSpace(name))
        {
            return new { error = "name is required." };
        }

        if (string.IsNullOrWhiteSpace(language))
        {
            return new { error = "language is required when no current app language is selected." };
        }

        if (parentCollectionId.Invalid)
        {
            return new { error = "parent_collection_id must be a valid id." };
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.CreateCollection,
            name = name.Trim(),
            language = language.Trim(),
            parent_collection_id = parentCollectionId.Value,
        }, JsonOptions);

        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.CreateCollection, payload));
        return new { queued = true, kind = PendingChangeKinds.CreateCollection, name = name.Trim() };
    }
}
