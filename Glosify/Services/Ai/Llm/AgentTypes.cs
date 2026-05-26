namespace Glosify.Services;

public sealed record AgentToolDeclaration(
    string Name,
    string Description,
    object ParametersJsonSchema);

public sealed record AgentRequest(
    string SystemInstruction,
    IReadOnlyList<AgentTurn> History,
    IReadOnlyList<AgentToolDeclaration> Tools);

public sealed record AgentTurn(string Role, string ContentJson);

public sealed record AgentTurnResult(
    string Text,
    IReadOnlyList<AgentFunctionCall> FunctionCalls);

public sealed record AgentFunctionCall(string Name, string ArgsJson, string? ThoughtSignature = null);

