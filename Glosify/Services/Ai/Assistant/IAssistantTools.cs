namespace Glosify.Services;

public interface IAssistantTools
{
    IReadOnlyList<AgentToolDeclaration> Declarations { get; }

    Task<object> ExecuteAsync(
        string name,
        string argsJson,
        AgentToolContext context,
        CancellationToken cancellationToken);
}
