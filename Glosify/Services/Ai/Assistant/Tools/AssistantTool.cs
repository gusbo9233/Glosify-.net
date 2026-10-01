using Glosify.Services.Ai.Generation;

namespace Glosify.Services.Ai.Assistant.Tools;

/// <summary>
/// Which assistant the conversation runs as. It fixes the system prompt and the tool list,
/// which both stay byte-identical for every request in the mode so the provider can reuse
/// its prompt cache.
/// </summary>
public enum AssistantMode
{
    /// <summary>Language learning: words, sentences, and translations.</summary>
    Language,

    /// <summary>Any subject: prompt-and-answer items.</summary>
    Freestyle,
}

internal enum AssistantToolKind
{
    /// <summary>Reads data. Safe to run in parallel with other reads.</summary>
    Read,

    /// <summary>Proposes changes that the runtime applies, or holds for approval.</summary>
    Write,

    /// <summary>Acts on the run itself: asking the user, planning, reading the request source.</summary>
    Control,
}

/// <summary>
/// One tool the assistant can call.
/// </summary>
/// <remarks>
/// A tool declares its schema and runs itself, but never saves anything: write tools return
/// the changes they propose, and the runtime decides whether they are applied now or held
/// for the user's approval. That keeps permission, journaling for Undo, and transactions in
/// one place instead of in every tool.
/// </remarks>
internal interface IAssistantTool
{
    string Name { get; }

    AssistantToolKind Kind { get; }

    bool Supports(AssistantMode mode);

    AgentToolDeclaration Declaration(AssistantMode mode);

    Task<ToolResult> ExecuteAsync(string argumentsJson, ToolContext context, CancellationToken cancellationToken);
}

/// <summary>A tool with typed arguments, parsed and validated against the schema it declares.</summary>
internal abstract class AssistantTool<TArgs> : IAssistantTool where TArgs : class
{
    public abstract string Name { get; }

    public abstract AssistantToolKind Kind { get; }

    public virtual bool Supports(AssistantMode mode) => true;

    protected abstract string Describe(AssistantMode mode);

    public AgentToolDeclaration Declaration(AssistantMode mode) =>
        new(Name, Describe(mode), ToolSchemas.For<TArgs>(), Strict: true);

    public async Task<ToolResult> ExecuteAsync(
        string argumentsJson,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        if (!ToolSchemas.TryParse<TArgs>(argumentsJson, out var args, out var problem))
        {
            return ToolResult.Fail(
                $"{Name}: invalid arguments",
                $"The {Name} arguments were invalid: {problem} Rewrite the call so it matches the schema.");
        }

        return await RunAsync(args, context, cancellationToken);
    }

    protected abstract Task<ToolResult> RunAsync(TArgs args, ToolContext context, CancellationToken cancellationToken);
}

/// <summary>What a tool knows about the conversation it serves.</summary>
internal sealed record ToolContext
{
    public required string UserId { get; init; }

    public required AssistantMode Mode { get; init; }

    public Guid? ThreadId { get; init; }
    public Guid? UserMessageId { get; init; }

    /// <summary>The quiz the chat is attached to, if any. Content tools default to it.</summary>
    public Guid? QuizId { get; init; }

    /// <summary>The learning language's display name, such as "Polish".</summary>
    public string? TargetLanguage { get; init; }

    public string? TargetLanguageCode { get; init; }

    /// <summary>The language translations are written in, when the app knows it.</summary>
    public string? SourceLanguage { get; init; }

    /// <summary>When set, edits and deletions may only target this word.</summary>
    public string? FocusedWordId { get; init; }

    public string? FocusedWordLabel { get; init; }

    public Guid? TranscriptId { get; init; }

    public Guid? BookDocumentId { get; init; }

    /// <summary>The content type the request asked for outright, used to refuse misfiled content.</summary>
    public AssistantContentKind RequestedContentKind { get; init; } = AssistantContentKind.Auto;

    /// <summary>The request text addressed by line number, when it was too long to inline.</summary>
    public SourceText? Source { get; init; }

    public bool IsFreestyle => Mode == AssistantMode.Freestyle;
}

/// <summary>
/// The outcome of one tool call: what the model is told, the line the user sees, and the
/// changes proposed for the runtime to apply.
/// </summary>
internal sealed class ToolResult
{
    public bool IsError { get; init; }

    /// <summary>Serialized as JSON for the model.</summary>
    public object? Output { get; init; }

    /// <summary>A short description for the activity list, in plain language.</summary>
    public required string Title { get; init; }

    public IReadOnlyList<PendingChange> Changes { get; init; } = [];

    /// <summary>The quiz read by this tool or targeted by its content changes.</summary>
    public Guid? QuizId { get; init; }

    /// <summary>A change to the run itself, applied by the runtime.</summary>
    public ToolEffect? Effect { get; init; }

    public static ToolResult Ok(string title, object? output, Guid? quizId = null) => new() { Title = title, Output = output, QuizId = quizId };

    public static ToolResult Fail(string title, string error, object? detail = null) => new()
    {
        Title = title,
        IsError = true,
        Output = detail is null ? new { error } : new { error, detail },
    };

    public static ToolResult Propose(
        string title,
        Guid? quizId,
        IReadOnlyList<PendingChange> changes,
        object? output = null) => new()
    {
        Title = title,
        QuizId = quizId,
        Changes = changes,
        Output = output,
    };
}

internal abstract record ToolEffect;

internal sealed record AskUserEffect(string Question, IReadOnlyList<string> Options, bool Multiple) : ToolEffect;

internal sealed record UpdatePlanEffect(IReadOnlyList<AssistantPlanItem> Items) : ToolEffect;

internal sealed record ReadSourceEffect(int FromLine, int ToLine, Guid? MessageId = null) : ToolEffect;

public sealed record AssistantPlanItem(string Text, string Status);

/// <summary>
/// A long request split into numbered lines, so the model can page through it and the runtime
/// can tell which lines it has read. Line numbers are far easier for a model to cite than
/// character offsets.
/// </summary>
internal sealed class SourceText
{
    private readonly string[] _lines;

    public SourceText(string text)
    {
        // Use the same bounded logical lines in previews, source catalogs, and read_source.
        // This also pages pasted prose that contains no physical line breaks.
        var lines = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length == 0) lines.Add(string.Empty);
            for (var start = 0; start < line.Length;)
            {
                var length = Math.Min(150, line.Length - start);
                if (start + length < line.Length && char.IsHighSurrogate(line[start + length - 1])) length--;
                lines.Add(line.Substring(start, length));
                start += length;
            }
        }
        _lines = lines.ToArray();
    }

    public int LineCount => _lines.Length;

    public IReadOnlyList<string> Lines => _lines;

    /// <summary>Lines <paramref name="from"/> through <paramref name="to"/>, one-based and inclusive.</summary>
    public IEnumerable<(int Number, string Text)> Range(int from, int to)
    {
        for (var number = Math.Max(1, from); number <= Math.Min(to, _lines.Length); number++)
        {
            yield return (number, _lines[number - 1]);
        }
    }
}
