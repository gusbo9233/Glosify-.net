using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProjectVault;

public record DetailLink(string TargetId, string Relation, string Label);
public record ContractValue(string Name, string Type, string Description, string? ModelId);
public record FunctionContract(string Purpose, string Signature, List<ContractValue> Inputs, ContractValue Output, List<string> Checks, bool Async, string Cancellation, List<string> SideEffects, List<string> Concepts, List<DocumentMarker> Concerns);
public record ModelField(string Name, string Type, string Description, List<string> Validation, string? ModelId);
public record SourceBinding(string Path, string DeclarationId, string Hash, int Line, int EndLine, string ReviewedCode);
public record SourceReference(string Id, string Path, int Line, int EndLine, string Hash);
public record DocumentMarker(string Category, string Reason, string Certainty);
public record DocumentNode(string Id, string Label, string Description, string Kind, List<string> Evidence, List<string> Links, List<DocumentMarker> Markers, double X, double Y) { public List<DetailLink> DetailLinks { get; init; } = []; }
public record DocumentTransition(string Id, string Source, string Target, string Label, string Trigger, string Condition, string Effect, string Description, List<string> Evidence) { public List<DetailLink> DetailLinks { get; init; } = []; public List<string> Inputs { get; init; } = []; public List<string> Outputs { get; init; } = []; public List<string> SideEffects { get; init; } = []; }
public record DocumentDiagram(string Id, string Title, string Kind, string Description, List<DocumentNode> Nodes, List<DocumentTransition> Transitions);
public record AuthoredDocument(string Id, string Title, string Summary, string Category, string Markdown, List<string> Links, List<SourceReference> Evidence, List<string> Dependencies, List<string> Unknowns, List<DocumentDiagram> Diagrams)
{
    public string Kind { get; init; } = "explanation";
    public List<DetailLink> DetailLinks { get; init; } = [];
    public FunctionContract? Contract { get; init; }
    public SourceBinding? PrimarySource { get; init; }
    public List<ModelField> Fields { get; init; } = [];
}
public record ReviewContext(string Branch, string Revision, string Worktree, Dictionary<string, string> Files, DateTimeOffset At, string Reason);
public record DocumentRevision(int Version, AuthoredDocument Document, ReviewContext Review);
public class DocumentEnvelope
{
    public string Id { get; set; } = "";
    public int Version { get; set; }
    public AuthoredDocument? Draft { get; set; }
    public DocumentRevision? Published { get; set; }
    public List<DocumentRevision> History { get; set; } = [];
}
public record SaveDocumentInput(AuthoredDocument Document, int ExpectedVersion, bool Publish);
public record ReviewDocumentInput(string Id, int ExpectedVersion, string Reason, List<SourceReference> Evidence);
public record DocumentationRequest(string Id, string Question, string? DocumentId, string? TargetId, string Status, List<string> ResultDocumentIds, string Response, int Version);
public record SaveRequestInput(DocumentationRequest Request, int ExpectedVersion);
public record DocumentNote(string Id, string DocumentId, string? TargetId, string Markdown);
public record DocumentPresentation(Dictionary<string, Position> Positions, List<string> Bookmarks);
public record DocumentImpact(string Id, string Title, string Status, List<string> ChangedFiles, bool ContextChanged, int Version);

/// <summary>Authored knowledge is independent of the optional extracted snapshot.</summary>
public sealed class Documents(Repository repo, string toolRoot)
{
    string PathFor(string relative) => repo.Absolute(".project-visualization/" + relative);
    static void Id(string id) { if (!Regex.IsMatch(id, "^[a-zA-Z0-9][a-zA-Z0-9_-]{0,95}$")) throw new ArgumentException("Use an identifier with 1–96 letters, numbers, hyphens or underscores."); }
    async Task<T?> Read<T>(string relative) => File.Exists(PathFor(relative)) ? JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(PathFor(relative)), Format.Json) : default;
    async Task Atomic<T>(string relative, T value)
    {
        var path = PathFor(relative); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value, Format.Json)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    FileStream Lease()
    {
        var path = PathFor("local/documents.lock"); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("Another document update is active. Read the latest version and retry."); }
    }
    public async Task<DocumentEnvelope?> Get(string id) { Id(id); return await Read<DocumentEnvelope>($"documents/{id}.json"); }
    async Task<List<DocumentEnvelope>> All()
    {
        var dir = PathFor("documents"); if (!Directory.Exists(dir)) return [];
        var result = new List<DocumentEnvelope>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.json").Order()) result.Add((await Get(System.IO.Path.GetFileNameWithoutExtension(path)))!);
        return result;
    }
    async Task<Dictionary<string, string>> Hashes(IEnumerable<string> paths)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in paths.Distinct())
        {
            if (System.IO.Path.IsPathRooted(path) || path.Contains('\\') || path.Split('/').Any(p => p is ".." or ".") || !Repository.Included(path)) throw new ArgumentException("Evidence must use a supported repository-relative source path: " + path);
            var absolute = repo.Absolute(path);
            result[path] = File.Exists(absolute) ? Format.Id(await File.ReadAllTextAsync(absolute)) : "missing";
        }
        return result;
    }
    public async Task<object> Source(string path, int line = 1, int count = 80)
    {
        var hashes = await Hashes([path]); if (hashes[path] == "missing") throw new ArgumentException("Source file does not exist.");
        var lines = (await repo.Read(path)).Split('\n');
        if (line < 1 || line > lines.Length) throw new ArgumentException("Source line is outside the file.");
        return new { path, hash = hashes[path], line, endLine = Math.Min(lines.Length, line + Math.Clamp(count, 1, 200) - 1), totalLines = lines.Length, text = string.Join('\n', lines.Skip(line - 1).Take(Math.Clamp(count, 1, 200))) };
    }
    async Task<ReviewContext> Validate(AuthoredDocument doc, string reason)
    {
        Id(doc.Id);
        if (string.IsNullOrWhiteSpace(doc.Title) || string.IsNullOrWhiteSpace(doc.Summary) || string.IsNullOrWhiteSpace(doc.Markdown)) throw new ArgumentException("Title, summary and explanation are required.");
        if (doc.Evidence.Count == 0) throw new ArgumentException("Publication requires source evidence; static indexing is not required.");
        var refs = doc.Evidence.Select(e => e.Id).ToHashSet();
        if (refs.Count != doc.Evidence.Count) throw new ArgumentException("Evidence identifiers must be unique.");
        if (doc.Kind is not ("workflow-overview" or "workflow" or "action" or "function" or "model" or "explanation")) throw new ArgumentException("Unknown document kind.");
        if (doc.Kind is "function" or "model" && doc.PrimarySource is null) throw new ArgumentException("Function and model pages require a primary source binding.");
        if (doc.Kind == "function" && doc.Contract is null) throw new ArgumentException("Function pages require a contract.");
        if (doc.Contract is not null && (string.IsNullOrWhiteSpace(doc.Contract.Purpose) || string.IsNullOrWhiteSpace(doc.Contract.Signature) || doc.Contract.Output is null || doc.Contract.Inputs is null || doc.Contract.Inputs.Append(doc.Contract.Output).Any(v => v is null || string.IsNullOrWhiteSpace(v.Name) || string.IsNullOrWhiteSpace(v.Type)))) throw new ArgumentException("Function contracts require a purpose, signature and named, typed input/output values.");
        if (doc.PrimarySource is not null)
        {
            if (doc.Kind == "function" && !doc.PrimarySource.DeclarationId.StartsWith("method:") && !doc.PrimarySource.DeclarationId.StartsWith("constructor:")) throw new ArgumentException("Function pages must bind a method or constructor.");
            if (doc.Kind == "model" && !doc.PrimarySource.DeclarationId.StartsWith("type:")) throw new ArgumentException("Model pages must bind a type declaration.");
            await new Declarations(repo).Validate(doc.PrimarySource);
        }
        var paths = doc.Dependencies.Concat(doc.Evidence.Select(e => e.Path)).Concat(doc.PrimarySource is null ? [] : new[] { doc.PrimarySource.Path }).ToList();
        var hashes = await Hashes(paths);
        if (hashes.Values.Contains("missing")) throw new ArgumentException("A documented dependency no longer exists. Reconcile the document before publishing.");
        foreach (var e in doc.Evidence)
        {
            Id(e.Id);
            if (hashes[e.Path] != e.Hash) throw new InvalidOperationException("Evidence changed: " + e.Path + ". Read current source and review the explanation.");
            if (e.Line < 1 || e.EndLine < e.Line || e.EndLine > (await repo.Read(e.Path)).Split('\n').Length) throw new ArgumentException("Evidence range is outside source: " + e.Path);
        }
        var targets = new HashSet<string>();
        var links = new List<string>(doc.Links);
        foreach (var diagram in doc.Diagrams)
        {
            Id(diagram.Id); if (!targets.Add(diagram.Id)) throw new ArgumentException("Diagram/item identifiers must be unique within a document.");
            if (diagram.Kind is not ("state-machine" or "process" or "architecture")) throw new ArgumentException("Diagram kind must be state-machine, process or architecture.");
            var nodes = diagram.Nodes.Select(n => n.Id).ToHashSet();
            foreach (var node in diagram.Nodes)
            {
                Id(node.Id); if (!targets.Add(node.Id)) throw new ArgumentException("Duplicate diagram item: " + node.Id);
                if (!double.IsFinite(node.X) || !double.IsFinite(node.Y)) throw new ArgumentException("Invalid node position.");
                if (node.Evidence.Any(e => !refs.Contains(e))) throw new ArgumentException("Node refers to unknown evidence.");
                links.AddRange(node.Links);
            }
            foreach (var edge in diagram.Transitions)
            {
                Id(edge.Id); if (!targets.Add(edge.Id)) throw new ArgumentException("Duplicate diagram item: " + edge.Id);
                if (!nodes.Contains(edge.Source) || !nodes.Contains(edge.Target)) throw new ArgumentException("Transition endpoints must belong to the diagram.");
                if (edge.Evidence.Any(e => !refs.Contains(e))) throw new ArgumentException("Transition refers to unknown evidence.");
                if (diagram.Kind == "state-machine" && (string.IsNullOrWhiteSpace(edge.Trigger) || string.IsNullOrWhiteSpace(edge.Effect))) throw new ArgumentException("State transitions need triggers and effects.");
            }
        }
        foreach (Match match in Regex.Matches(doc.Markdown + "\n" + string.Join('\n', doc.Diagrams.SelectMany(d => d.Nodes).Select(n => n.Description)), @"(?:#document=|\[\[)([A-Za-z0-9_-]+)")) links.Add(match.Groups[1].Value);
        foreach (var detail in DetailLinks(doc))
        {
            if (detail.Relation is not ("workflow" or "expands" or "calls" or "uses-model" or "related")) throw new ArgumentException("Unknown detail-link relation.");
            var target = detail.TargetId == doc.Id ? doc : (await Get(detail.TargetId))?.Published?.Document;
            if (target is null) throw new ArgumentException("Detail target has not been published: " + detail.TargetId);
            var expected = detail.Relation switch { "workflow" => "workflow", "expands" => "action", "calls" => "function", "uses-model" => "model", _ => null };
            if (expected is not null && target.Kind != expected) throw new ArgumentException("Detail link " + detail.Relation + " requires a " + expected + " page.");
            links.Add(detail.TargetId);
        }
        var modelIds = (doc.Contract?.Inputs ?? []).Append(doc.Contract?.Output).Where(x => x?.ModelId is not null).Select(x => x!.ModelId!).Concat(doc.Fields.Where(f => f.ModelId is not null).Select(f => f.ModelId!));
        foreach (var modelId in modelIds) if ((modelId == doc.Id ? doc : (await Get(modelId))?.Published?.Document)?.Kind != "model") throw new ArgumentException("Contract/model field references must target published model pages: " + modelId);
        foreach (var link in links.Distinct()) if (link != doc.Id && (await Get(link))?.Published is null) throw new ArgumentException("Linked document has not been published: " + link);
        var identity = await repo.Identity();
        return new(identity.Branch, identity.Revision, identity.Worktree, hashes, DateTimeOffset.UtcNow, reason);
    }
    async Task CheckContext(ReviewContext baseline)
    {
        var after = await Hashes(baseline.Files.Keys); var identity = await repo.Identity();
        if (after.Any(x => baseline.Files[x.Key] != x.Value) || identity.Branch != baseline.Branch || identity.Worktree != baseline.Worktree || identity.Revision != baseline.Revision) throw new InvalidOperationException("Source context changed during publication. Review and retry.");
    }
    public async Task<DocumentEnvelope> Save(SaveDocumentInput input)
    {
        Id(input.Document.Id); using var lease = Lease();
        var envelope = await Get(input.Document.Id) ?? new DocumentEnvelope { Id = input.Document.Id };
        if (envelope.Version != input.ExpectedVersion) throw new InvalidOperationException("Document revision conflict. Read the current version before saving.");
        if (input.Publish)
        {
            var review = await Validate(input.Document, "Agent published a source-reviewed document.");
            await CheckContext(review);
            if (envelope.Published is not null) envelope.History.Add(envelope.Published);
            envelope.Published = new(envelope.Version + 1, input.Document, review); envelope.Draft = null;
        }
        else envelope.Draft = input.Document;
        envelope.Version++;
        await Atomic("local/tool.json", new { toolRoot });
        await Atomic($"documents/{envelope.Id}.json", envelope);
        return envelope;
    }
    public async Task<DocumentEnvelope> Review(ReviewDocumentInput input)
    {
        Id(input.Id); using var lease = Lease();
        var envelope = await Get(input.Id) ?? throw new ArgumentException("Document does not exist.");
        if (envelope.Version != input.ExpectedVersion) throw new InvalidOperationException("Document revision conflict.");
        if (envelope.Published is null || string.IsNullOrWhiteSpace(input.Reason)) throw new ArgumentException("A published document and a review rationale are required.");
        var doc = envelope.Published.Document;
        if (!doc.Evidence.Select(e => e.Id).ToHashSet().SetEquals(input.Evidence.Select(e => e.Id))) throw new ArgumentException("Review must reconcile all existing evidence identifiers. Publish a revision to change the explanation or dependencies.");
        if (doc.PrimarySource is not null)
        {
            var currentSource = await new Declarations(repo).Resolve(doc.PrimarySource.Path, doc.PrimarySource.DeclarationId);
            if (currentSource.ReviewedCode != doc.PrimarySource.ReviewedCode) throw new InvalidOperationException("Function/model code changed. Publish a reviewed document revision rather than recording an unchanged review.");
            doc = doc with { PrimarySource = currentSource };
        }
        doc = doc with { Evidence = input.Evidence };
        var review = await Validate(doc, input.Reason); await CheckContext(review);
        envelope.History.Add(envelope.Published); envelope.Published = new(++envelope.Version, doc, review);
        await Atomic($"documents/{input.Id}.json", envelope); return envelope;
    }
    public async Task<List<DocumentImpact>> Impacts()
    {
        var identity = await repo.Identity(); var result = new List<DocumentImpact>();
        foreach (var envelope in await All())
        {
            var p = envelope.Published;
            if (p is null) { result.Add(new(envelope.Id, envelope.Draft?.Title ?? envelope.Id, "Draft", [], false, envelope.Version)); continue; }
            var now = await Hashes(p.Review.Files.Keys);
            var changed = now.Where(x => x.Value != p.Review.Files[x.Key]).Select(x => x.Key).ToList();
            var context = identity.Branch != p.Review.Branch || identity.Worktree != p.Review.Worktree;
            result.Add(new(envelope.Id, p.Document.Title, changed.Count > 0 || context ? "Needs review" : "Reviewed", changed, context, envelope.Version));
        }
        return result;
    }
    public async Task Initialize()
    {
        using var lease = Lease();
        await Atomic("local/tool.json", new { toolRoot = System.IO.Path.GetFullPath(toolRoot) });
        var ignore = PathFor(".gitignore");
        if (!File.Exists(ignore)) await File.WriteAllTextAsync(ignore, "local/\n*.tmp\n");
    }
    public async Task<object> Status()
    {
        var impacts = await Impacts(); var identity = await repo.Identity();
        return new { project = System.IO.Path.GetFileName(repo.Root), fresh = impacts.All(i => i.Status != "Needs review"), status = impacts.Any(i => i.Status == "Needs review") ? "Needs review" : "Reviewed dependencies unchanged", identity.Branch, identity.Revision, documents = impacts, meaning = "Dependency checks assist agent review; they do not prove semantic correctness or complete coverage." };
    }
    public async Task<object> Library()
    {
        var impacts = (await Impacts()).ToDictionary(x => x.Id);
        return (await All()).Select(e => new { e.Id, e.Version, title = (e.Published?.Document ?? e.Draft)?.Title, summary = (e.Published?.Document ?? e.Draft)?.Summary, category = (e.Published?.Document ?? e.Draft)?.Category, kind = (e.Published?.Document ?? e.Draft)?.Kind ?? "explanation", published = e.Published is not null, hasDraft = e.Draft is not null, impact = impacts[e.Id] }).ToList();
    }
    static IEnumerable<DetailLink> DetailLinks(AuthoredDocument doc) => doc.DetailLinks.Concat(doc.Diagrams.SelectMany(d => d.Nodes.SelectMany(n => n.DetailLinks).Concat(d.Transitions.SelectMany(e => e.DetailLinks))));
    static IEnumerable<string> Targets(AuthoredDocument doc) => doc.Links.Concat(doc.Diagrams.SelectMany(d => d.Nodes).SelectMany(n => n.Links)).Concat(DetailLinks(doc).Select(l => l.TargetId))
        .Concat((doc.Contract?.Inputs ?? []).Append(doc.Contract?.Output).Where(x => x?.ModelId is not null).Select(x => x!.ModelId!))
        .Concat(doc.Fields.Where(f => f.ModelId is not null).Select(f => f.ModelId!))
        .Concat(Regex.Matches(doc.Markdown, @"(?:#document=|\[\[)([A-Za-z0-9_-]+)").Select(m => m.Groups[1].Value));
    public async Task<object> Backlinks(string id)
    {
        Id(id);
        return (await All()).Where(e => e.Published is not null && e.Id != id && Targets(e.Published.Document).Contains(id))
            .Select(e => new { e.Id, e.Published!.Document.Title, e.Published.Document.Kind }).ToList();
    }
    public async Task<object> BoundSource(string id, int? version = null)
    {
        var envelope = await Get(id) ?? throw new ArgumentException("Document does not exist.");
        var published = version is null || envelope.Published?.Version == version ? envelope.Published : envelope.History.FirstOrDefault(h => h.Version == version);
        var reviewed = published?.Document.PrimarySource ?? throw new ArgumentException("This revision has no primary source binding.");
        var candidates = (await new Declarations(repo).Find(reviewed.Path)).Where(c => c.DeclarationId == reviewed.DeclarationId).ToList();
        var current = candidates.Count == 1 ? candidates[0] : null;
        var status = candidates.Count > 1 ? "ambiguous" : current is null ? "unresolved" : current == reviewed ? "unchanged" : "changed";
        return new { status, reviewed, current, message = status switch { "unchanged" => "Source matches the reviewed declaration.", "changed" => "Source changed. The reviewed excerpt is retained; current code is shown separately.", "ambiguous" => "Several declarations match. No replacement was selected.", _ => "The declaration is missing, renamed or no longer resolvable. No replacement was selected." } };
    }
    public async Task<List<DocumentationRequest>> Requests()
    {
        var dir = PathFor("requests"); if (!Directory.Exists(dir)) return [];
        var result = new List<DocumentationRequest>(); foreach (var path in Directory.EnumerateFiles(dir, "*.json").Order()) result.Add((await Read<DocumentationRequest>("requests/" + System.IO.Path.GetFileName(path)))!); return result;
    }
    public async Task<DocumentationRequest> SaveRequest(SaveRequestInput input)
    {
        var value = input.Request; Id(value.Id); using var lease = Lease();
        var old = await Read<DocumentationRequest>($"requests/{value.Id}.json");
        if ((old?.Version ?? 0) != input.ExpectedVersion) throw new InvalidOperationException("Request revision conflict.");
        if (string.IsNullOrWhiteSpace(value.Question) || value.Status is not ("open" or "in-progress" or "partial" or "answered")) throw new ArgumentException("A question and a valid request status are required.");
        if (value.DocumentId is not null && await Get(value.DocumentId) is null) throw new ArgumentException("Request document does not exist.");
        foreach (var id in value.ResultDocumentIds) if ((await Get(id))?.Published is null) throw new ArgumentException("Result documents must be published.");
        if (value.Status == "answered" && (value.ResultDocumentIds.Count == 0 || string.IsNullOrWhiteSpace(value.Response))) throw new ArgumentException("Answered requests require published results and a response.");
        value = value with { Version = input.ExpectedVersion + 1 }; await Atomic($"requests/{value.Id}.json", value); return value;
    }
    public async Task<object> Notes(string documentId)
    {
        var envelope = await Get(documentId) ?? throw new ArgumentException("Document does not exist.");
        var targets = (envelope.Published?.Document.Diagrams ?? []).SelectMany(d => d.Nodes.Select(n => n.Id).Concat(d.Transitions.Select(e => e.Id)).Append(d.Id)).ToHashSet();
        var notes = await Read<List<DocumentNote>>($"document-notes/{documentId}.json") ?? [];
        return notes.Select(n => new { note = n, unresolved = n.TargetId is not null && !targets.Contains(n.TargetId) }).ToList();
    }
    public async Task<DocumentNote> SaveNote(DocumentNote note)
    {
        Id(note.Id); Id(note.DocumentId); using var lease = Lease();
        if (await Get(note.DocumentId) is null) throw new ArgumentException("Document does not exist.");
        var notes = await Read<List<DocumentNote>>($"document-notes/{note.DocumentId}.json") ?? [];
        notes.RemoveAll(n => n.Id == note.Id); notes.Add(note);
        await Atomic($"document-notes/{note.DocumentId}.json", notes); return note;
    }
    public async Task<DocumentPresentation> Presentation(string id) { Id(id); return await Read<DocumentPresentation>($"document-layouts/{id}.json") ?? new([], []); }
    public async Task<DocumentPresentation> SavePresentation(string id, DocumentPresentation presentation)
    {
        Id(id); using var lease = Lease();
        if (presentation.Positions.Any(p => !double.IsFinite(p.Value.X) || !double.IsFinite(p.Value.Y))) throw new ArgumentException("Invalid presentation position.");
        await Atomic($"document-layouts/{id}.json", presentation); return presentation;
    }
}
