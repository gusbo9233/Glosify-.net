using System.Text.Json;
using Glosify.Services.Ai.Generation;

namespace Glosify.Services.Ai.Assistant.Tools;

/// <summary>
/// One tool the assistant can call.
/// </summary>
/// <remarks>
/// A tool declares itself and executes itself. Keeping those two together is the point of
/// the interface: the previous design held 40-odd declarations, five hand-maintained lists
/// of which ones each assistant profile is offered, and a 40-arm switch, all in one 2,991
/// line class, with nothing tying an entry in the switch to an entry in the lists.
/// </remarks>
internal interface IAssistantTool
{
    AgentToolDeclaration Declaration { get; }

    /// <summary>
    /// The name the model calls this tool by.
    /// </summary>
    string Name => Declaration.Name;

    /// <summary>
    /// Extra names that route here. Only <c>create_vocabulary_quiz</c> uses this: it is an
    /// older name for <c>create_quiz</c> that the dispatcher still accepted but no profile
    /// ever declared, and dropping it would break any saved chat mid-tool-call.
    /// </summary>
    IReadOnlyList<string> Aliases => [];

    Task<object> ExecuteAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken);
}
