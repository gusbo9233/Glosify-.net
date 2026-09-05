using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProjectVault;

public static class Format
{
    public static string AnalyzerPath { get; set; } = "";
    public static string EngineVersion => typeof(Format).Assembly.ManifestModule.ModuleVersionId + (File.Exists(AnalyzerPath) ? Id(File.ReadAllText(AnalyzerPath)) : "");
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static string Id(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24].ToLowerInvariant();
}
public record Evidence(string Path, int Line, string Hash);
public record Concern(string Category, string Certainty, string Reason);
public record Field(string Name, string Type);
public class Element
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "function";
    public string Layer { get; set; } = "functions";
    public string Group { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Status { get; set; } = "extracted";
    public string? Signature { get; set; }
    public string? Route { get; set; }
    public string? Verb { get; set; }
    public bool Async { get; set; }
    public bool EntryPoint { get; set; }
    public List<Field> Inputs { get; set; } = [];
    public string? Output { get; set; }
    public List<string> Checks { get; set; } = [];
    public List<string> Concepts { get; set; } = [];
    public List<Concern> Concerns { get; set; } = [];
    public List<Evidence> Evidence { get; set; } = [];
}
public record Relation(string Id, string Source, string Target, string Kind, string Status = "extracted", int? Order = null);
public record FlowStep(string Id, string Label, string Kind, string? ElementId, Evidence? Evidence);
public record FlowEdge(string Source, string Target, string Label);
public class Workflow
{
    public string Id { get; set; } = "";
    public string EntryId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Coverage { get; set; } = "partial";
    public List<string> Gaps { get; set; } = [];
    public List<string> Members { get; set; } = [];
    public List<FlowStep> Steps { get; set; } = [];
    public List<FlowEdge> Edges { get; set; } = [];
    public List<FlowStep> States { get; set; } = [];
    public List<FlowEdge> Transitions { get; set; } = [];
}
public class Snapshot
{
    public int SchemaVersion { get; set; } = 1;
    public string EngineVersion { get; set; } = Format.EngineVersion;
    public string Id { get; set; } = "";
    public string Project { get; set; } = "";
    public string Branch { get; set; } = "";
    public string Revision { get; set; } = "";
    public string Worktree { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public SortedDictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);
    public List<Element> Elements { get; set; } = [];
    public List<Relation> Relations { get; set; } = [];
    public List<Workflow> Workflows { get; set; } = [];
    public List<string> Diagnostics { get; set; } = [];
}
public record Freshness(bool Fresh, string Status, string Fingerprint, string Branch, string Revision, int ChangedFiles, string? Error);
public record GraphEdit(string Kind, string? Source, string? Target, string? ElementId, string? Label);
public record Criterion(string Text, bool Verified, string Evidence);
public class Proposal
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string BaseSnapshot { get; set; } = "";
    public List<string> AffectedIds { get; set; } = [];
    public string Narrative { get; set; } = "";
    public List<GraphEdit> Edits { get; set; } = [];
    public List<Criterion> Criteria { get; set; } = [];
    public string Status { get; set; } = "draft";
    public string? ResultSnapshot { get; set; }
    public string Deviations { get; set; } = "";
    public int Version { get; set; }
}
public record Annotation(string ElementId, string Markdown, string SnapshotId, string EvidenceStatus = "user", List<Evidence>? Evidence = null);
public record Layout(Dictionary<string, Position> Positions, List<string> Bookmarks);
public record Position(double X, double Y);
