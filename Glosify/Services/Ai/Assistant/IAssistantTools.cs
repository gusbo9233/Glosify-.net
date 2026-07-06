using Glosify.Services.Ai.Llm;
namespace Glosify.Services.Ai.Assistant;

public interface IAssistantTools
{
    IReadOnlyList<AgentToolDeclaration> Declarations { get; }
    IReadOnlyList<AgentToolDeclaration> GlobalDeclarations { get; }

    Task<object> ExecuteAsync(
        string name,
        string argsJson,
        AgentToolContext context,
        CancellationToken cancellationToken);
}
