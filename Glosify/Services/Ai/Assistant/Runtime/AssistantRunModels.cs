using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Glosify.Services.Ai.Assistant.Tools;
using Glosify.Services.Ai.Generation;

namespace Glosify.Services.Ai.Assistant.Runtime;

public sealed class AssistantRuntimeOptions
{
    /// <summary>Model calls per execution window. Saving changes renews the window automatically.</summary>
    [Range(2, 60)]
    public int MaxModelCalls { get; set; } = 24;

    [Range(10, 900)]
    public int WindowSeconds { get; set; } = 300;

    /// <summary>
    /// Estimated prompt tokens a request may carry before older tool output is cleared and older
    /// turns are summarized. Credits are charged per token, so this bounds the cost of every step.
    /// </summary>
    [Range(8_000, 1_000_000)]
    public int ContextTokenBudget { get; set; } = 32_000;

    /// <summary>The most recent tool output, in estimated tokens, that is never cleared.</summary>
    [Range(1_000, 500_000)]
    public int ProtectedToolOutputTokens { get; set; } = 8_000;

    [Range(1, 8)]
    public int Workers { get; set; } = 2;
}

public sealed class JevOptions
{
    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "jev-1.13.0";

    [Range(0, 1)]
    public double FindingThreshold { get; set; } = .8;
}

public sealed record AssistantRunInput(
    [param: Required, StringLength(50_000)] string Message,
    Guid? ContextQuizId = null,
    string? FocusedWordId = null,
    AssistantDocumentContext? DocumentContext = null,
    Guid? TranscriptId = null,
    Guid? BookDocumentId = null,
    AssistantTranscriptPageContext? TranscriptContext = null);

public sealed record AssistantRunStartInput(
    [param: Required, StringLength(100, MinimumLength = 1)] string IdempotencyKey,
    [param: Required] AssistantRunInput Request);

/// <summary>A command on a run. Revision is the one the user saw; only some commands require it to match.</summary>
public sealed record AssistantRunCommand(
    long Revision,
    [param: StringLength(50_000)] string? Message = null,
    bool Always = false,
    IReadOnlyList<string>? Answers = null);

public sealed record AssistantRunView(
    Guid Id,
    Guid ThreadId,
    string Status,
    string? Reason,
    long Revision,
    Guid TurnId,
    Guid UserMessageId,
    Guid? MessageId,
    int SavedChanges,
    int Steps,
    bool CanUndo,
    bool Undone,
    IReadOnlyList<AssistantPlanItem> Plan,
    AssistantQuestionView? Question,
    AssistantApprovalView? Approval,
    IReadOnlyList<AssistantPartView> Parts,
    IReadOnlyList<AssistantRunArtifact> Artifacts);

/// <summary>One visible part of a reply: text, or a line of tool activity.</summary>
public sealed record AssistantPartView(
    Guid Id,
    Guid MessageId,
    string Type,
    string? Text,
    string? Tool,
    string? Title,
    string? State,
    object? Review = null);

public sealed record AssistantQuestionView(Guid PartId, string Question, IReadOnlyList<string> Options, bool Multiple);

public sealed record AssistantApprovalView(Guid PartId, IReadOnlyList<AssistantPendingChangeView> Changes, IReadOnlyList<string> Kinds);

public sealed record AssistantRunArtifact(Guid Id, string Name, string Status);

public sealed record AssistantUndoResult(int Undone, int Kept);

public sealed class AssistantRunConflictException(string message) : InvalidOperationException(message);

internal static class RunPhases
{
    public const string Model = "model";
    public const string Tools = "tools";
    public const string Finish = "finish";
}

/// <summary>Loop bookkeeping for one run. The conversation itself lives in messages and parts.</summary>
internal sealed class AssistantRunState
{
    public bool Initialized { get; set; }
    public string Phase { get; set; } = RunPhases.Model;

    /// <summary>The number of the last model call, counted across the whole run.</summary>
    public int Step { get; set; }

    public int NoProgress { get; set; }

    public int WindowSavedChanges { get; set; }
    public Dictionary<string, string> ReadResults { get; set; } = [];

    /// <summary>Times the run asked the model to finish unread source or open plan items.</summary>
    public int Reminders { get; set; }

    /// <summary>The last reminder given. The same one again is not repeated: the model already declined it.</summary>
    public string? LastReminder { get; set; }

    public bool FinalCallMade { get; set; }

    /// <summary>Quiz placements proposed at the end of the run, whose approval returns to Finish.</summary>
    public bool FinishingPlacements { get; set; }

    public int LastInputTokens { get; set; }
    public int ChangeSequence { get; set; }
    public RunFacts? Facts { get; set; }
    public List<Guid> PendingSteering { get; set; } = [];
    public HashSet<string> AppliedCommands { get; set; } = [];

    /// <summary>Quiz content fingerprints as of the last model call, for detecting a user's concurrent edits.</summary>
    public Dictionary<Guid, string> Fingerprints { get; set; } = [];

    public List<Guid> CreatedQuizzes { get; set; } = [];

    /// <summary>Quizzes built privately whose requested collection is public, moved when the run ends.</summary>
    public Dictionary<Guid, Guid> Placements { get; set; } = [];

    /// <summary>Merged, inclusive line ranges of the request source that the model has read.</summary>
    public List<int[]> SourceRead { get; set; } = [];

    public List<string> RecentErrors { get; set; } = [];

    /// <summary>Analytics keys: unique per turn, and the invocation behind each model step.</summary>
    public int InvocationSequence { get; set; }
    public int ToolSequence { get; set; }
    public Dictionary<int, Guid> Invocations { get; set; } = [];
}

/// <summary>What the run knows about its context, fixed when it starts.</summary>
internal sealed record RunFacts(
    AssistantMode Mode,
    AssistantAgentProfile Profile,
    Guid? QuizId,
    string? TargetLanguage,
    string? TargetLanguageCode,
    string? SourceLanguage,
    string ReplyLanguage,
    string? FocusedWordId,
    string? FocusedWordLabel,
    Guid? TranscriptId,
    Guid? BookDocumentId,
    AssistantContentKind RequestedContentKind,
    int? SourceLines);

internal static class RunJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    internal static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    internal static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options)!;

    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>History content in the shape <see cref="OpenAiMessageMapper"/> replays.</summary>
    internal static AgentTurn Text(string role, string text) =>
        new(role, Write(new { parts = new[] { new { kind = "text", text } } }));
}
