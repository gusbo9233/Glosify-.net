using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Glosify.Services.Ai.Generation;

namespace Glosify.Services.Ai.Assistant.Runtime;

public sealed class AssistantRuntimeOptions
{
    public bool Enabled { get; set; }
    public bool WebEnabled { get; set; }
    public bool CoverageEnabled { get; set; }
    [Range(1, 24)] public int MaxModelCalls { get; set; } = 24;
    [Range(1000, 64000)] public int MaxTokens { get; set; } = 64000;
    [Range(10, 300)] public int WindowSeconds { get; set; } = 300;
}
public sealed class JevOptions
{
    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "jev-1.13.0";
    [Range(0, 1)] public double FindingThreshold { get; set; } = .8;
}
public sealed record AssistantTaskInput(
    [param: Required, StringLength(50000)] string Message,
    Guid? ContextQuizId = null, string? FocusedWordId = null,
    AssistantDocumentContext? DocumentContext = null,
    Guid? TranscriptId = null, Guid? BookDocumentId = null,
    AssistantTranscriptPageContext? TranscriptContext = null);
public sealed record AssistantTaskStartInput(
    [param: Required, StringLength(100, MinimumLength = 1)] string IdempotencyKey,
    [param: Required] AssistantTaskInput Request);
public sealed record AssistantTaskCommand(
    long Revision, [param: StringLength(50000)] string? Message = null);
public sealed record AssistantTaskView(Guid Id, Guid ThreadId, string Status, string? Reason,
    long Revision, int SavedChanges, int ModelCalls, int Tokens, object? Result,
    IReadOnlyList<AssistantTaskActivity> Activity, int EvaluatedCalls, int TotalCalls,
    IReadOnlyList<AssistantPendingChangeView> ApprovalChanges, IReadOnlyList<AssistantTaskArtifact> Artifacts);
public sealed record AssistantTaskArtifact(Guid Id, string Name, string Status);
public sealed record AssistantTaskActivity(Guid Id, string Tool, string Status, string EvaluationStatus, object? Evaluation);

internal sealed class AssistantRuntimeState
{
    public HashSet<string> AppliedCommands { get; set; } = [];
    public bool Initialized { get; set; }
    public List<AgentTurn> History { get; set; } = [];
    /// <summary>Position of this task's request in <see cref="History"/>, after earlier chat text.</summary>
    public int RequestIndex { get; set; }
    public List<PendingChange> PendingChanges { get; set; } = [];
    public List<SourceSection> Sources { get; set; } = [];
    public List<string> CoveredSections { get; set; } = [];
    public Dictionary<string, string> Exclusions { get; set; } = [];
    public Dictionary<string, Guid> DraftCollections { get; set; } = [];
    public Dictionary<string, Guid> DraftQuizzes { get; set; } = [];
    public Dictionary<string, List<string>> SavedDraftItems { get; set; } = [];
    public HashSet<string> AppliedChanges { get; set; } = [];
    public List<AgentFunctionCall> Calls { get; set; } = [];
    public List<object> CallResults { get; set; } = [];
    public int NextCall { get; set; }
    public int CallSequence { get; set; }
    public int SteeringCount { get; set; }
    public bool NeedsCorrection { get; set; }
    public Dictionary<string, string> CoverageEvidence { get; set; } = [];
    public int NoProgress { get; set; }
    public Guid? FinalMessageId { get; set; }
    public Guid TurnId { get; set; } = Guid.NewGuid();
    public Dictionary<int, string> PrefetchedReads { get; set; } = [];
    public Dictionary<string, string> ReadCache { get; set; } = [];
    public List<string> LastErrors { get; set; } = [];
    public string? ResourceFingerprint { get; set; }
    public string? FinalText { get; set; }
}
internal sealed record SourceSection(string Id, string Text);
internal static class RuntimeJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    internal static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    internal static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options)!;
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static AgentTurn Text(string role, string text) => new(role, Write(new { parts = new[] { new { kind = "text", text } } }));
    internal static int EstimateTokens(AgentRequest request)
    {
        // Count what the provider sees, not the multiply escaped checkpoint envelope.
        var characters = request.SystemInstruction.Length + (request.ContextInstruction?.Length ?? 0)
            + Write(AgentToolFilter.Narrow(request.Tools, request.AllowedToolNames)).Length;
        foreach (var turn in request.History)
        {
            var content = Read<JsonElement>(turn.ContentJson);
            if (content.TryGetProperty("outputItemsJson", out var output) && output.GetArrayLength() > 0)
                characters += output.EnumerateArray().Sum(x => x.GetString()?.Length ?? 0);
            else if (content.TryGetProperty("parts", out var parts))
                foreach (var part in parts.EnumerateArray())
                    foreach (var key in new[] { "text", "name", "argsJson", "responseJson" })
                        if (part.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                            characters += value.GetString()!.Length;
        }
        return 200 + characters / 3;
    }
    internal static List<SourceSection> Split(string text)
    {
        var result = new List<SourceSection>();
        for (var offset = 0; offset < text.Length;)
        {
            var length = Math.Min(2400, text.Length - offset);
            if (offset + length < text.Length)
            {
                var newline = text.LastIndexOf('\n', offset + length - 1, length);
                if (newline > offset) length = newline - offset + 1;
                else if (char.IsHighSurrogate(text[offset + length - 1])) length--;
            }
            result.Add(new($"section-{result.Count + 1}", text.Substring(offset, length)));
            offset += length;
        }
        return result;
    }
}
