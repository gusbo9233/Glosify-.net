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
public sealed record AgentRequest(
    string SystemInstruction,
    IReadOnlyList<AgentTurn> History,
    IReadOnlyList<AgentToolDeclaration> Tools,
    string? Model = null,
    AssistantAgentProfile Profile = AssistantAgentProfile.General,
    string? ContextInstruction = null);

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
