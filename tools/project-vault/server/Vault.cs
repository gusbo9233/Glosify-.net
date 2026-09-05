using System.Text.Json;
using System.Text.RegularExpressions;
namespace ProjectVault;

public sealed class Vault(Repository repo, string toolRoot)
{
    readonly SemaphoreSlim gate = new(1);
    string FilePath(string relative) => repo.Absolute(".project-visualization/" + relative);
    static void ValidateId(string id) { if (!Regex.IsMatch(id, "^[a-f0-9]{24}$")) throw new ArgumentException("Invalid element or proposal identifier."); }
    async Task<T?> Read<T>(string path) => File.Exists(FilePath(path)) ? JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(FilePath(path)), Format.Json) : default;
    async Task Write<T>(string path, T value)
    {
        var target = FilePath(path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, Format.Json)); File.Move(temporary, target, true); } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    async Task Text(string path, string value)
    {
        var target = FilePath(path); Directory.CreateDirectory(Path.GetDirectoryName(target)!); var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temp, value); File.Move(temp, target, true); } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public Task<Snapshot?> Current() => Read<Snapshot>("current.json");
    public async Task<Freshness> Status()
    {
        var snapshot = await Current(); var files = await repo.Inventory(); var identity = await repo.Identity();
        var fingerprint = Repository.Fingerprint(files, identity.Revision, identity.Branch, identity.Worktree);
        var changed = snapshot is null ? files.Count : files.Keys.Union(snapshot.Files.Keys).Count(k => files.GetValueOrDefault(k) != snapshot.Files.GetValueOrDefault(k));
        var failure = await Read<Failure>("local/failure.json");
        return new(snapshot?.Id == fingerprint && failure is null, snapshot is null ? "not indexed" : failure is not null ? "blocked" : snapshot.Id == fingerprint ? "current" : "stale", fingerprint, identity.Branch, identity.Revision, changed, failure?.Message);
    }
    public async Task<Snapshot> Refresh(bool force = false)
    {
        await gate.WaitAsync();
        try
        {
            // Cross-process lock coordinates the HTTP host, MCP process and CLI refresh.
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath("local/refresh.lock"))!);
            using var lease = new FileStream(FilePath("local/refresh.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            await Write("local/tool.json", new { toolRoot });
            var files = await repo.Inventory(); var old = await Current(); var identity = await repo.Identity();
            var expected = Repository.Fingerprint(files, identity.Revision, identity.Branch, identity.Worktree);
            if (!force && old?.Id == expected) { await ClearFailure(); return old; }
            var snapshot = await new Analyzer(repo, toolRoot).Analyze(files);
            var after = await repo.Inventory(); var currentIdentity = await repo.Identity();
            if (snapshot.Id != Repository.Fingerprint(after, currentIdentity.Revision, currentIdentity.Branch, currentIdentity.Worktree)) throw new InvalidOperationException("Source changed during analysis. Refresh again when the implementation step is complete.");
            if (snapshot.Elements.Select(x => x.Id).Distinct().Count() != snapshot.Elements.Count) throw new InvalidOperationException("Duplicate element identities detected.");
            var ids = snapshot.Elements.Select(x => x.Id).ToHashSet();
            if (snapshot.Relations.Any(r => !ids.Contains(r.Source) || !ids.Contains(r.Target))) throw new InvalidOperationException("Unresolved graph reference.");
            if (old is not null) await Write("snapshots/" + old.Id + ".json", old);
            await Write("snapshots/" + snapshot.Id + ".json", snapshot);
            await Write("current.json", snapshot);
            await Text(".gitignore", "local/\n*.tmp\n");
            if (!File.Exists(FilePath("README.md"))) await Text("README.md", "# Project Vault\n\nGenerated facts live in current.json and snapshots/. User notes live in notes/.\nProposals keep their own base snapshot and never change extracted facts.\nDo not edit generated snapshots. Refresh with the project-vault CLI.\nLocal caches and failures are intentionally not shared.\n");
            await ClearFailure(); return snapshot;
        }
        catch (Exception ex) { await Write("local/failure.json", new Failure(ex is IOException ? "Another refresh or filesystem error prevented publication. Retry after the active refresh finishes." : ex.Message, DateTimeOffset.UtcNow)); throw; }
        finally { gate.Release(); }
    }
    Task ClearFailure() { var path = FilePath("local/failure.json"); if (File.Exists(path)) File.Delete(path); return Task.CompletedTask; }
    public async Task<object> Element(string id)
    {
        ValidateId(id); var snapshot = await Current() ?? throw new ArgumentException("Project has not been indexed.");
        return new { element = snapshot.Elements.FirstOrDefault(x => x.Id == id), unresolved = snapshot.Elements.All(x => x.Id != id), relations = snapshot.Relations.Where(x => x.Source == id || x.Target == id), workflows = snapshot.Workflows.Where(w => w.Members.Contains(id)).Select(w => new { w.Id, w.Name, w.Coverage }), annotation = await Note(id), interpretation = await Interpretation(id), mentions = await Mentions(id) };
    }
    public async Task<Annotation> Note(string id)
    {
        ValidateId(id); var path = FilePath("notes/" + id + ".md"); return new(id, File.Exists(path) ? await File.ReadAllTextAsync(path) : "", (await Current())?.Id ?? "");
    }
    public async Task SaveNote(Annotation note) { ValidateId(note.ElementId); if (note.Markdown.Length > 100_000) throw new ArgumentException("Note is too large."); await Text("notes/" + note.ElementId + ".md", note.Markdown); }
    public async Task<object?> Interpretation(string id)
    {
        var value = await Read<Annotation>("interpretations/" + id + ".json"); if (value is null) return null;
        var snapshot = await Current(); var stale = value.Evidence is null || value.Evidence.Any(e => snapshot?.Files.GetValueOrDefault(e.Path) != e.Hash);
        return new { value, stale };
    }
    public async Task SaveInterpretation(Annotation value)
    {
        ValidateId(value.ElementId); var snapshot = await Current() ?? throw new ArgumentException("Index first.");
        if (value.SnapshotId != snapshot.Id || !(await Status()).Fresh) throw new InvalidOperationException("Interpretation must reference the current source snapshot.");
        if (value.Evidence is null || value.Evidence.Count == 0 || value.Evidence.Any(e => e.Line < 1 || snapshot.Files.GetValueOrDefault(e.Path) != e.Hash)) throw new ArgumentException("Supply valid source evidence with file hash and line.");
        if (snapshot.Elements.All(e => e.Id != value.ElementId)) throw new ArgumentException("Interpretation target is not in the current map.");
        foreach (var evidence in value.Evidence) if (evidence.Line > (await repo.Read(evidence.Path)).Count(c => c == '\n') + 1) throw new ArgumentException("Evidence line is outside its source file.");
        await Write("interpretations/" + value.ElementId + ".json", value with { EvidenceStatus = "interpreted" });
    }
    async Task<List<string>> Mentions(string id)
    {
        var directory = FilePath("notes"); if (!Directory.Exists(directory)) return [];
        var result = new List<string>(); foreach (var path in Directory.EnumerateFiles(directory, "*.md")) if ((await File.ReadAllTextAsync(path)).Contains("[[" + id)) result.Add(Path.GetFileNameWithoutExtension(path)); return result;
    }
    public async Task<List<object>> Notes()
    {
        var directory = FilePath("notes"); if (!Directory.Exists(directory)) return [];
        var snapshot = await Current(); var ids = snapshot?.Elements.Select(e => e.Id).ToHashSet() ?? [];
        var result = new List<object>(); foreach (var path in Directory.EnumerateFiles(directory, "*.md")) { var id = Path.GetFileNameWithoutExtension(path); result.Add(new { id, unresolved = !ids.Contains(id) }); }
        return result;
    }
    public async Task<Layout> GetLayout() => await Read<Layout>("layout.json") ?? new([], []);
    public async Task SaveLayout(Layout layout)
    {
        if (layout.Positions.Count > 100_000 || layout.Positions.Any(p => !double.IsFinite(p.Value.X) || !double.IsFinite(p.Value.Y))) throw new ArgumentException("Invalid layout."); await Write("layout.json", layout);
    }
    public async Task<List<Proposal>> Proposals()
    {
        var directory = FilePath("proposals"); if (!Directory.Exists(directory)) return [];
        var result = new List<Proposal>(); foreach (var path in Directory.EnumerateFiles(directory, "*.json")) { var p = await Read<Proposal>("proposals/" + Path.GetFileName(path)); if (p is not null) result.Add(p); }
        return result;
    }
    public async Task<Proposal> SaveProposal(Proposal proposal)
    {
        await gate.WaitAsync(); try
        {
            var snapshot = await Current() ?? throw new ArgumentException("Index the project first.");
            if (proposal.Id.Length == 0) { proposal.Id = Format.Id(Guid.NewGuid().ToString()); proposal.BaseSnapshot = snapshot.Id; proposal.Status = "draft"; proposal.Version = 0; }
            ValidateId(proposal.Id);
            var old = await Read<Proposal>("proposals/" + proposal.Id + ".json");
            if (old is not null && (old.Version != proposal.Version || old.BaseSnapshot != proposal.BaseSnapshot)) throw new InvalidOperationException("Proposal was changed by another writer. Reload before saving.");
            if (string.IsNullOrWhiteSpace(proposal.Title) || proposal.Title.Length > 200 || proposal.Narrative.Length > 100_000) throw new ArgumentException("Supply a title and a narrative under 100,000 characters.");
            if (proposal.Status is not ("draft" or "in-progress" or "partial" or "implemented")) throw new ArgumentException("Invalid proposal status.");
            if (proposal.Edits.Any(e => e.Kind is not ("add-node" or "remove-node" or "connect" or "disconnect"))) throw new ArgumentException("Invalid graph edit.");
            if (proposal.Status == "implemented")
            {
                if (!(await Status()).Fresh || proposal.ResultSnapshot != snapshot.Id || proposal.Criteria.Count == 0 || proposal.Criteria.Any(c => !c.Verified || string.IsNullOrWhiteSpace(c.Evidence)) || !string.IsNullOrWhiteSpace(proposal.Deviations)) throw new ArgumentException("Implementation requires a fresh result snapshot, evidenced acceptance criteria, and no outstanding deviations.");
            }
            proposal.Version++;
            await Write("proposals/" + proposal.Id + ".json", proposal);
            await Text("proposals/" + proposal.Id + ".md", "# " + proposal.Title + "\n\n" + proposal.Narrative + "\n");
            return proposal;
        }
        finally { gate.Release(); }
    }
    public async Task<object> Compare(string baseId)
    {
        ValidateId(baseId); var before = await Read<Snapshot>("snapshots/" + baseId + ".json") ?? throw new ArgumentException("Base snapshot is not available."); var after = await Current() ?? throw new ArgumentException("No current snapshot.");
        var a = before.Elements.ToDictionary(e => e.Id); var b = after.Elements.ToDictionary(e => e.Id);
        var oldEdges = before.Relations.Select(r => r.Id).ToHashSet(); var newEdges = after.Relations.Select(r => r.Id).ToHashSet();
        return new { baseSnapshot = baseId, resultSnapshot = after.Id, added = b.Values.Where(e => !a.ContainsKey(e.Id)), removed = a.Values.Where(e => !b.ContainsKey(e.Id)), changed = b.Values.Where(e => a.TryGetValue(e.Id, out var old) && JsonSerializer.Serialize(old, Format.Json) != JsonSerializer.Serialize(e, Format.Json)), addedRelations = after.Relations.Where(r => !oldEdges.Contains(r.Id)), removedRelations = before.Relations.Where(r => !newEdges.Contains(r.Id)) };
    }
    record Failure(string Message, DateTimeOffset At);
}
