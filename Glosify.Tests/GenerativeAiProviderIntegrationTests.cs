using Glosify.Services.Ai.Generation;
using Glosify.Services.Ai.Llm;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Glosify.Tests;

public sealed class GenerativeAiProviderIntegrationTests
{
    [Fact]
    public void Checked_in_configuration_binds_a_distinct_user_selectable_model_menu()
    {
        using var factory = new WebApplicationFactory<Program>();

        var resolver = factory.Services.GetRequiredService<IGenerativeAiModelResolver>();

        // Both entries must exist as deployments in the glosify-assistant project.
        // DeepSeek-V4-Flash is deployed there too but is page-translation fallback only,
        // so it is deliberately absent from the user-selectable menu.
        Assert.Equal("gpt-5.6-luna", resolver.DefaultAssistantModel);
        Assert.Collection(
            resolver.AssistantModels,
            model =>
            {
                Assert.Equal("gpt-5.6-luna", model.Deployment);
                Assert.Equal("OpenAI", model.Provider);
                Assert.Equal(1m, model.CreditMultiplier);
            },
            model =>
            {
                Assert.Equal("grok-4.3", model.Deployment);
                Assert.Equal("xAI", model.Provider);
                // 1x, not 2x: grok-4.3 costs less per output token than the default
                // deployment, so charging double steered users to the pricier model.
                Assert.Equal(1m, model.CreditMultiplier);
            });
    }

    [Theory]
    [InlineData(GenerativeAiOptions.FoundryProvider, typeof(FoundryGenerativeAiClient))]
    [InlineData(GenerativeAiOptions.GeminiProvider, typeof(GeminiGenerativeAiClient))]
    public void Explicit_provider_setting_selects_exactly_one_adapter(
        string provider,
        Type expectedType)
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["GenerativeAi:Provider"] = provider,
                        ["Gemini:ApiKey"] = "rollback-test-key",
                        // This test isolates adapter selection. Separate validator tests
                        // prove that a budgeted Gemini rollback fails closed without prices.
                        ["AiUsage:MonthlyBudget:Enabled"] = "false",
                    }));
            });
        using var scope = factory.Services.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IGenerativeAiClient>();

        Assert.IsType(expectedType, client);
    }
}
