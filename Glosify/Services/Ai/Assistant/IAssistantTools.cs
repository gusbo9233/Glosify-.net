using Glosify.Services.Ai.Generation;
namespace Glosify.Services.Ai.Assistant;

public interface IAssistantTools
{
    IReadOnlyList<AgentToolDeclaration> Declarations { get; }
    IReadOnlyList<AgentToolDeclaration> GlobalDeclarations { get; }
    IReadOnlyList<AgentToolDeclaration> CustomQuizBuilderDeclarations { get; }
    IReadOnlyList<AgentToolDeclaration> QuizAssistantDeclarations { get; }
    IReadOnlyList<AgentToolDeclaration> LibrarianDeclarations { get; }

    /// <summary>
    /// The declared name of the tool registered under <paramref name="name"/>, or null when no
    /// tool answers to it.
    /// </summary>
    /// <remarks>
    /// Aliases exist so a chat saved mid-tool-call still resolves, which means a per-turn
    /// allowlist cannot compare returned names to declared names directly. Resolving through
    /// the registry first keeps the alias working without widening what the turn permits.
    /// </remarks>
    string? ResolveCanonicalName(string name);

    Task<object> ExecuteAsync(
        string name,
        string argsJson,
        AgentToolContext context,
        CancellationToken cancellationToken);
}
