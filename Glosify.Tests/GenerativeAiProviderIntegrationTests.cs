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

        Assert.Equal("gpt-5.4-mini", resolver.DefaultAssistantModel);
        Assert.Collection(
            resolver.AssistantModels,
            model =>
            {
                Assert.Equal("gpt-5.4-mini", model.Deployment);
                Assert.Equal("OpenAI", model.Provider);
                Assert.Equal(1m, model.CreditMultiplier);
            },
            model =>
            {
                Assert.Equal("grok-4-1-fast-non-reasoning", model.Deployment);
                Assert.Equal("xAI", model.Provider);
                Assert.Equal(1m, model.CreditMultiplier);
            },
            model =>
            {
                Assert.Equal("grok-4-1-fast-reasoning", model.Deployment);
                Assert.Equal("xAI", model.Provider);
                Assert.Equal(2m, model.CreditMultiplier);
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
                    }));
            });
        using var scope = factory.Services.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IGenerativeAiClient>();

        Assert.IsType(expectedType, client);
    }
}
