using Glosify.Services.Ai.Generation;

namespace Glosify.Services.Ai.Assistant.Tools;

/// <summary>
/// The tools each assistant mode is offered, in a fixed order.
/// </summary>
/// <remarks>
/// The declaration list for a mode never varies with the page, the quiz, or the request: the
/// provider caches a prompt prefix that includes the tool definitions, and a list that shifts
/// per turn would invalidate everything after it. Context decides what a tool acts on, not
/// whether the model can see it.
/// </remarks>
internal sealed class AssistantToolbox
{
    private static readonly string[] Order =
    [
        "quiz_overview",
        "list_items",
        "search_items",
        "add_items",
        "edit_items",
        "delete_items",
        "create_quiz",
        "list_library",
        "create_collection",
        "rename_collection",
        "move_quiz",
        "move_collection",
        "list_books",
        "get_book_pages",
        "search_book_pages",
        "list_saved_transcripts",
        "get_saved_transcript",
        "list_saved_translation_sessions",
        "get_saved_translation_session",
        "read_source",
        "update_plan",
        "ask_user",
    ];

    private readonly Dictionary<AssistantMode, IReadOnlyList<IAssistantTool>> _byMode;

    public AssistantToolbox(IEnumerable<IAssistantTool> tools)
    {
        var all = tools.ToArray();
        _byMode = Enum.GetValues<AssistantMode>().ToDictionary(
            mode => mode,
            mode =>
            {
                var available = all.Where(tool => tool.Supports(mode)).ToArray();
                foreach (var duplicate in available.GroupBy(tool => tool.Name).Where(group => group.Count() > 1))
                {
                    throw new InvalidOperationException($"Two assistant tools claim '{duplicate.Key}' in {mode} mode.");
                }

                var unordered = available.Select(tool => tool.Name).Except(Order).ToArray();
                if (unordered.Length > 0)
                {
                    throw new InvalidOperationException($"Assistant tools missing from the toolbox order: {string.Join(", ", unordered)}.");
                }

                return (IReadOnlyList<IAssistantTool>)available.OrderBy(tool => Array.IndexOf(Order, tool.Name)).ToArray();
            });
    }

    public IReadOnlyList<IAssistantTool> Tools(AssistantMode mode) => _byMode[mode];

    public IReadOnlyList<AgentToolDeclaration> Declarations(AssistantMode mode) =>
        _byMode[mode].Select(tool => tool.Declaration(mode)).ToArray();

    public IAssistantTool? Find(AssistantMode mode, string name) =>
        _byMode[mode].FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.Ordinal));
}

public static class AssistantToolServiceExtensions
{
    /// <summary>Registers every assistant tool in this assembly and the toolbox that orders them.</summary>
    public static IServiceCollection AddAssistantTools(this IServiceCollection services)
    {
        foreach (var type in typeof(AssistantToolbox).Assembly.GetTypes()
            .Where(type => typeof(IAssistantTool).IsAssignableFrom(type) && type is { IsAbstract: false, IsInterface: false })
            .OrderBy(type => type.Name, StringComparer.Ordinal))
        {
            services.AddScoped(typeof(IAssistantTool), type);
        }

        services.AddScoped<AssistantToolbox>();
        return services;
    }
}
