using Glosify.Data;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantToolSurfaceRegressionTests
{
    [Fact]
    public void Production_profiles_offer_no_custom_quiz_tools()
    {
        using var context = CreateContext();
        var tools = AssistantToolFactory.Create(context);
        var declarations = tools.Declarations
            .Concat(tools.GlobalDeclarations)
            .Concat(tools.QuizAssistantDeclarations)
            .Concat(tools.LibrarianDeclarations)
            .Concat(tools.FreestyleQuizAssistantDeclarations)
            .Concat(tools.FreestyleLibrarianDeclarations)
            .DistinctBy(tool => tool.Name)
            .ToArray();

        Assert.Contains(declarations, tool => tool.Name == "create_vocabulary_quiz");
        Assert.DoesNotContain(declarations, tool =>
            tool.Name.Contains("custom_quiz", StringComparison.Ordinal)
            || tool.Name is "add_choice" or "add_checkbox" or "add_text_input"
                or "add_word_bank" or "add_submit_button" or "add_feedback_message");

        foreach (var profile in Enum.GetValues<AssistantAgentProfile>())
        {
            Assert.DoesNotContain("create_custom_quiz", AssistantProfileInstructions.Get(profile));
        }

        Assert.StartsWith("2026-08-28.custom-quiz-retirement", AssistantProfileInstructions.Version);
    }

    [Fact]
    public async Task Every_declared_tool_is_dispatchable()
    {
        await using var context = CreateContext();
        var tools = AssistantToolFactory.Create(context);
        var declared = tools.GlobalDeclarations
            .Concat(tools.QuizAssistantDeclarations)
            .Concat(tools.LibrarianDeclarations)
            .Concat(tools.FreestyleQuizAssistantDeclarations)
            .Concat(tools.FreestyleLibrarianDeclarations)
            .Concat(tools.Declarations)
            .Select(declaration => declaration.Name)
            .Distinct(StringComparer.Ordinal);

        foreach (var name in declared)
        {
            var result = await tools.ExecuteAsync(
                name,
                "{}",
                new AgentToolContext { UserId = "user-1" },
                CancellationToken.None);

            Assert.DoesNotContain(
                "Unknown tool",
                System.Text.Json.JsonSerializer.Serialize(result),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Get_saved_transcript_accepts_page_and_time_coordinates()
    {
        using var context = CreateContext();
        var declaration = Assert.Single(
            AssistantToolFactory.Create(context).GlobalDeclarations,
            item => item.Name == "get_saved_transcript");
        var properties = System.Text.Json.JsonSerializer
            .SerializeToElement(declaration.ParametersJsonSchema)
            .GetProperty("properties");

        Assert.True(properties.TryGetProperty("page", out _));
        Assert.True(properties.TryGetProperty("at_time", out _));
        Assert.True(properties.TryGetProperty("offset", out _));
        Assert.True(properties.TryGetProperty("stream", out _));
    }

    private static GlosifyContext CreateContext() =>
        new(new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
}
