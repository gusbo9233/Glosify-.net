using System.Reflection;
using System.Text.Json;
namespace ProjectVault;

public record DocumentIdInput(string Id);
public record DeclarationFileInput(string Path);
public record DeclarationInput(string Path, string DeclarationId);
public record BoundSourceInput(string Id, int? Version);
public record SourceInput(string Path, int Line, int Count);
public static class DocumentTools
{
    static object Schema(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is Type inner) return new { anyOf = new object[] { Schema(inner), new { type = "null" } } };
        if (type == typeof(string)) return new { type = "string" };
        if (type == typeof(bool)) return new { type = "boolean" };
        if (type == typeof(int)) return new { type = "integer" };
        if (type == typeof(double)) return new { type = "number" };
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)) return new { type = "array", items = Schema(type.GetGenericArguments()[0]) };
        var nullability = new NullabilityInfoContext();
        var properties = type.GetProperties().ToDictionary(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name), p => nullability.Create(p).ReadState == NullabilityState.Nullable ? (object)new { anyOf = new object[] { Schema(p.PropertyType), new { type = "null" } } } : Schema(p.PropertyType));
        return new { type = "object", properties, required = properties.Keys.ToArray(), additionalProperties = false };
    }
    static object Tool<T>(string name, string description) => new { name, description, inputSchema = Schema(typeof(T)) };
    static object Empty(string name, string description) => new { name, description, inputSchema = new { type = "object", properties = new { }, additionalProperties = false } };
    public static object[] Definitions => [
        Tool<DeclarationFileInput>("vault_declarations", "List exact C# declaration identities in a supplied file, including overloads. This is focused source reference, not document generation."),
        Tool<DeclarationInput>("vault_declaration", "Read a complete C# declaration and its exact primary-source binding for a function/model page. Never guess declaration IDs; use vault_declarations."),
        Tool<BoundSourceInput>("vault_document_source", "Compare a document revision's preserved source excerpt with the currently resolved declaration. Missing/ambiguous declarations have no selected replacement."),
        Empty("vault_documents", "List curated authored documents, drafts and review status. Start here; content is driven by questions, not analysis."),
        Tool<DocumentIdInput>("vault_document", "Read a document, draft, published revision and review history."),
        Tool<SaveDocumentInput>("vault_save_document", "Save a draft or atomically publish an agent-authored document. Use expectedVersion=0 to create. Evidence hashes come from vault_source. Conceptual states need not match code symbols. Publication never requires an index."),
        Tool<ReviewDocumentInput>("vault_review_document", "Record an evidenced review when the explanation remains accurate. Supply reconciled evidence with all original IDs and a reason. If meaning changed, publish an updated document instead."),
        Empty("vault_document_status", "Check authored documentation dependencies and branch/worktree context. Index freshness cannot mark documents reviewed."),
        Empty("vault_document_impacts", "Find documents potentially affected by source changes. Also consider semantic impacts beyond listed dependencies."),
        Tool<SourceInput>("vault_source", "Read repository source with current evidence hash and line numbers. count is capped at 200. No static index required."),
        Empty("vault_document_requests", "Read user documentation requests. Requests describe existing behavior; they do not authorize application code changes."),
        Tool<SaveRequestInput>("vault_save_request", "Create or update a documentation request with an expected version. Mark answered only with published result IDs and response; retain unanswered questions as partial."),
        Tool<DocumentIdInput>("vault_document_notes", "Read user annotations, including unresolved targets, for a document."),
        Tool<DocumentNote>("vault_save_document_note", "Save a separate user Markdown annotation without changing authored facts.")
    ];
    static T Input<T>(JsonElement value) => value.Deserialize<T>(Format.Json) ?? throw new ArgumentException("Missing typed input.");
    public static async Task<object?> Call(Documents docs, Declarations declarations, string name, JsonElement a) => name switch
    {
        "vault_declarations" => await declarations.List(Input<DeclarationFileInput>(a).Path),
        "vault_declaration" => await declarations.Resolve(Input<DeclarationInput>(a).Path, Input<DeclarationInput>(a).DeclarationId),
        "vault_document_source" => await docs.BoundSource(Input<BoundSourceInput>(a).Id, Input<BoundSourceInput>(a).Version),
        "vault_documents" => await docs.Library(),
        "vault_document" => await docs.Get(Input<DocumentIdInput>(a).Id),
        "vault_save_document" => await docs.Save(Input<SaveDocumentInput>(a)),
        "vault_review_document" => await docs.Review(Input<ReviewDocumentInput>(a)),
        "vault_document_status" => await docs.Status(),
        "vault_document_impacts" => await docs.Impacts(),
        "vault_source" => await Source(docs, Input<SourceInput>(a)),
        "vault_document_requests" => await docs.Requests(),
        "vault_save_request" => await docs.SaveRequest(Input<SaveRequestInput>(a)),
        "vault_document_notes" => await docs.Notes(Input<DocumentIdInput>(a).Id),
        "vault_save_document_note" => await docs.SaveNote(Input<DocumentNote>(a)),
        _ => throw new ArgumentException("Unknown document tool")
    };
    static Task<object> Source(Documents docs, SourceInput input) => docs.Source(input.Path, input.Line, input.Count);
}
