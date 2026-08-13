namespace Glosify.Services.Ai.Generation;

public sealed record AgentToolDeclaration(
    string Name,
    string Description,
    object ParametersJsonSchema);

/// <summary>
/// Selects which authored Foundry agent should answer a turn. Each profile pairs a
/// persisted agent (its instructions live in Foundry, not in this repository) with the
/// narrow tool set that profile is allowed to call.
/// </summary>
public enum AssistantAgentProfile
{
    /// <summary>No authored agent: full tool surface, instructions built in code.</summary>
    General,

    /// <summary>A quiz page: its words, sentences, and custom quizzes.</summary>
    QuizAssistant,

    /// <summary>No quiz selected: the library, and quizzes built from source material.</summary>
    Librarian,

    /// <summary>The custom quiz creator: element tools only, no quiz-creation tools.</summary>
    CustomQuizBuilder,
}

/// <param name="SystemInstruction">
/// The complete instruction, used when no persisted agent backs the profile.
/// </param>
/// <param name="ContextInstruction">
/// Only the facts that change per turn (open quiz, languages). Appended to a persisted
/// agent's authored instructions, which carry everything static.
/// </param>
/// <param name="AllowedToolNames">
/// The tool names this turn may offer, or null for no restriction.
/// </param>
/// <remarks>
/// A persisted agent owns which tools exist and how they are described, and that stays true:
/// <paramref name="AllowedToolNames"/> can only remove entries from whichever declaration list
/// is in force. It never adds one, and never replaces an authored schema — an authored tool
/// the application did not allow for this turn is simply not offered.
/// </remarks>
public sealed record AgentRequest(
    string SystemInstruction,
    IReadOnlyList<AgentTurn> History,
    IReadOnlyList<AgentToolDeclaration> Tools,
    string? Model = null,
    AssistantAgentProfile Profile = AssistantAgentProfile.General,
    string? ContextInstruction = null,
    IReadOnlySet<string>? AllowedToolNames = null);

/// <summary>
/// Applies a turn's tool allowlist to whichever declaration list is in force.
/// </summary>
public static class AgentToolFilter
{
    public static IReadOnlyList<AgentToolDeclaration> Narrow(
        IReadOnlyList<AgentToolDeclaration> declarations,
        IReadOnlySet<string>? allowedNames) =>
        allowedNames is null
            ? declarations
            : declarations.Where(tool => allowedNames.Contains(tool.Name)).ToArray();
}

public sealed record AgentTurn(string Role, string ContentJson);

public sealed record AgentTurnResult(
    string Text,
    IReadOnlyList<AgentFunctionCall> FunctionCalls)
{
    public AgentInvocationMetadata? Metadata { get; init; }
}

public sealed record AgentInvocationMetadata(
    string Provider,
    string Model,
    string? ResponseId,
    AiTokenUsage Usage,
    string? AgentName = null,
    string? AgentVersion = null,
    string? EffectiveRequestJson = null);

public sealed record AgentFunctionCall(string Name, string ArgsJson, string? ThoughtSignature = null)
{
    public string? CallId { get; init; }
}
