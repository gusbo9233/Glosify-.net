using Glosify.Services.Ai.Assistant.Tools;
using Glosify.Services.Ai.Generation;

namespace Glosify.Services.Ai.Assistant;

/// <summary>
/// Resolves a tool by name and builds the declaration list each assistant profile is offered.
/// </summary>
/// <remarks>
/// The five profile lists are still curated by hand, because their order is deliberate — the
/// custom quiz builder puts get_custom_quiz first, the librarian puts
/// create_custom_quiz_from_content before the element tools — and that ordering is part of
/// the prompt. What is no longer hand-maintained is the link between a list and the code
/// that runs: the lists name tool <em>types</em>, so a tool that is renamed, removed or
/// misspelled is a compile error rather than a tool the model is offered and cannot call.
/// </remarks>
public sealed class AssistantToolRegistry : IAssistantTools
{
    private readonly Dictionary<string, IAssistantTool> _byName;

    internal AssistantToolRegistry(IEnumerable<IAssistantTool> tools)
    {
        var all = tools.ToArray();
        _byName = new Dictionary<string, IAssistantTool>(StringComparer.Ordinal);
        foreach (var tool in all)
        {
            Add(tool.Name, tool);
            foreach (var alias in tool.Aliases)
            {
                Add(alias, tool);
            }
        }

        var byType = all.ToDictionary(tool => tool.GetType());
        IReadOnlyList<AgentToolDeclaration> Surface(IReadOnlyList<Type> types) =>
            types.Select(type => byType[type].Declaration).ToArray();

        Declarations = Surface(AssistantToolSurfaces.Quiz);
        QuizAssistantDeclarations = Surface(AssistantToolSurfaces.QuizAssistant);
        GlobalDeclarations = Surface(AssistantToolSurfaces.Global);
        LibrarianDeclarations = Surface(AssistantToolSurfaces.Librarian);
        CustomQuizBuilderDeclarations = Surface(AssistantToolSurfaces.CustomQuizBuilder);
    }

    public IReadOnlyList<AgentToolDeclaration> Declarations { get; }
    public IReadOnlyList<AgentToolDeclaration> GlobalDeclarations { get; }
    public IReadOnlyList<AgentToolDeclaration> CustomQuizBuilderDeclarations { get; }
    public IReadOnlyList<AgentToolDeclaration> QuizAssistantDeclarations { get; }
    public IReadOnlyList<AgentToolDeclaration> LibrarianDeclarations { get; }

    public string? ResolveCanonicalName(string name) =>
        _byName.TryGetValue(name, out var tool) ? tool.Name : null;

    public Task<object> ExecuteAsync(
        string name,
        string argsJson,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        if (!_byName.TryGetValue(name, out var tool))
        {
            return Task.FromResult<object>(new { error = $"Unknown tool: {name}" });
        }

        return tool.ExecuteAsync(ToolArguments.ParseArgs(argsJson), context, cancellationToken);
    }

    private void Add(string name, IAssistantTool tool)
    {
        if (!_byName.TryAdd(name, tool))
        {
            throw new InvalidOperationException(
                $"Two assistant tools claim the name '{name}': "
                + $"{_byName[name].GetType().Name} and {tool.GetType().Name}.");
        }
    }
}
