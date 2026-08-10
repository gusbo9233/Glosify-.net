using Glosify.Data;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.RealtimeTranslation;
using Microsoft.Extensions.DependencyInjection;

namespace Glosify.Tests;

/// <summary>
/// Builds the assistant tool surface over one context, through the real DI registration.
/// </summary>
/// <remarks>
/// AssistantTools used to be a single 2,991-line class these tests could new up. It is now
/// ~40 IAssistantTool classes behind AssistantToolRegistry, so the tests compose it the way
/// the app does. Going through AddAssistantTools rather than hand-listing the tools means a
/// tool that is written but never registered fails here too.
/// </remarks>
internal static class AssistantToolFactory
{
    public static IAssistantTools Create(GlosifyContext context) =>
        new ServiceCollection()
            .AddSingleton(context)
            .AddSingleton<TimeProvider>(TimeProvider.System)
            .AddSingleton<IRealtimeTranslationTranscriptService, RealtimeTranslationTranscriptService>()
            .AddAssistantTools()
            .BuildServiceProvider()
            .GetRequiredService<IAssistantTools>();
}
