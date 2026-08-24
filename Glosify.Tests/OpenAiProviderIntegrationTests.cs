using Glosify.Services.Ai.Generation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Glosify.Tests;

public sealed class OpenAiProviderIntegrationTests
{
    [Fact]
    public void Application_registers_only_the_direct_openai_adapter()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var scope = factory.Services.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IGenerativeAiClient>();

        Assert.IsType<OpenAiGenerativeAiClient>(client);
    }
}
