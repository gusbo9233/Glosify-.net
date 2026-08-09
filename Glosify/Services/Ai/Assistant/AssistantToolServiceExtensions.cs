using Glosify.Services.Ai.Assistant.Tools;

namespace Glosify.Services.Ai.Assistant;

public static class AssistantToolServiceExtensions
{
    /// <summary>
    /// Registers every <see cref="IAssistantTool"/> in this assembly, plus the registry
    /// that dispatches to them.
    /// </summary>
    /// <remarks>
    /// Discovery is by assembly scan rather than a list, so adding a tool class is the
    /// whole job — but the tool still has to be named in <see cref="AssistantToolSurfaces"/>
    /// before any assistant profile is offered it, and that list is typed, so a tool
    /// cannot be silently half-added.
    /// </remarks>
    public static IServiceCollection AddAssistantTools(this IServiceCollection services)
    {
        var toolTypes = typeof(AssistantToolServiceExtensions).Assembly
            .GetTypes()
            .Where(type => typeof(IAssistantTool).IsAssignableFrom(type)
                && type is { IsAbstract: false, IsInterface: false })
            .OrderBy(type => type.Name, StringComparer.Ordinal);

        services.AddScoped<CustomQuizToolStore>();
        foreach (var type in toolTypes)
        {
            services.AddScoped(typeof(IAssistantTool), type);
        }

        services.AddScoped<IAssistantTools>(provider =>
            new AssistantToolRegistry(provider.GetServices<IAssistantTool>()));

        return services;
    }
}
