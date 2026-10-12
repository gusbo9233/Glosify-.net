namespace Glosify.Services.Ai.Generation;

public sealed record AgentToolDeclaration(
    string Name,
    string Description,
    object ParametersJsonSchema, bool Strict = false);

/// <summary>
/// Selects the code-owned profile instruction and narrow tool surface for a turn.
/// </summary>
public enum AssistantAgentProfile
{
    /// <summary>Full code-owned tool surface and general instruction.</summary>
    General,

    /// <summary>A quiz page: its words and sentences.</summary>
    QuizAssistant,

    /// <summary>No quiz selected: the library, and quizzes built from source material.</summary>
    Librarian,

    /// <summary>A Freestyle quiz page with generic prompt-and-answer items.</summary>
    FreestyleQuizAssistant,

    /// <summary>The Freestyle library with no quiz selected.</summary>
    FreestyleLibrarian,

}

/// <param name="SystemInstruction">
/// The complete code-owned instruction.
/// </param>
/// <param name="ContextInstruction">
/// Extra instruction text appended to <paramref name="SystemInstruction"/>. Anything that
/// varies per request belongs in <see cref="TrailingInstruction"/> instead, so the cached
/// prefix is not rewritten.
/// </param>
/// <param name="AllowedToolNames">
/// The declared tools this request may call, or null for no restriction. Enforced with the
/// provider's allowed_tools choice; the declaration list itself is always sent whole.
/// </param>
/// <param name="CaptureEffectiveRequest">
/// Whether the client should return the composed request as
/// <see cref="AgentInvocationMetadata.EffectiveRequestJson"/>.
/// </param>
/// <remarks>
/// <paramref name="AllowedToolNames"/> can only narrow the code-owned declaration list. It can
/// never widen the tool surface.
/// </remarks>
public sealed record AgentRequest(
    string SystemInstruction,
    IReadOnlyList<AgentTurn> History,
    IReadOnlyList<AgentToolDeclaration> Tools,
    AssistantAgentProfile Profile = AssistantAgentProfile.General,
    string? ContextInstruction = null,
    IReadOnlySet<string>? AllowedToolNames = null,
    bool CaptureEffectiveRequest = false, bool DurableExecution = false, int? MaxOutputTokens = null)
{
    /// <summary>
    /// Volatile notes sent as the last input item, after the history. Keeping per-step state
    /// here instead of in the instructions leaves the cached prompt prefix intact.
    /// </summary>
    public string? TrailingInstruction { get; init; }

    public AgentToolChoice ToolChoice { get; init; } = AgentToolChoice.Auto;
}

public enum AgentToolChoice
{
    Auto,

    /// <summary>Tools stay declared, keeping the cached prefix, but the model must reply in text.</summary>
    None,
}

public sealed record AgentTurn(string Role, string ContentJson);

public sealed record AgentTurnResult(
    string Text,
    IReadOnlyList<AgentFunctionCall> FunctionCalls)
{
    public AgentInvocationMetadata? Metadata { get; init; }
    public IReadOnlyList<string> OutputItemsJson { get; init; } = [];
}

public sealed record AgentInvocationMetadata(
    string Provider,
    string Model,
    string? ResponseId,
    AiTokenUsage Usage,
    string? AgentName = null,
    string? AgentVersion = null,
    /// <summary>
    /// The composed request, populated only when the caller asked for it via
    /// <see cref="AgentRequest.CaptureEffectiveRequest"/>. It restates the instruction, the
    /// whole replayed history and every tool schema, so building it unconditionally would
    /// serialize the largest object in the turn on a path that usually discards it.
    /// </summary>
    string? EffectiveRequestJson = null);

public sealed record AgentFunctionCall(string Name, string ArgsJson, string? ThoughtSignature = null)
{
    public string? CallId { get; init; }
}
