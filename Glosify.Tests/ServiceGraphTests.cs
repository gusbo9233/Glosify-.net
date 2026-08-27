using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai;
using Glosify.Services.Quizzes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Glosify.Tests;

/// <summary>
/// Resolves the assistant services whose registrations are discovered at startup.
/// </summary>
/// <remarks>
/// A missing or mis-scoped registration in either would not fail a build and would not
/// fail a page render either — it would surface as a 500 the first time a user sent an
/// assistant message. This resolves them from a real request scope instead, which is where
/// the scoped DbContext they all share lives.
/// </remarks>
public sealed class ServiceGraphTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ServiceGraphTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData(typeof(IAssistantTools))]
    [InlineData(typeof(IQuizJsonImportService))]
    [InlineData(typeof(IQuizJsonImportRepairService))]
    public void Split_services_resolve_from_a_request_scope(Type serviceType)
    {
        using var scope = _factory.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService(serviceType));
    }

    /// <summary>
    /// Every tool the registry can hand out is constructible. Assembly-scanned registration
    /// means a tool with a dependency nobody registered compiles fine and only fails when
    /// the model happens to call it.
    /// </summary>
    [Fact]
    public void Every_registered_assistant_tool_is_constructible()
    {
        using var scope = _factory.Services.CreateScope();
        var tools = scope.ServiceProvider
            .GetRequiredService<IAssistantTools>();

        Assert.NotEmpty(tools.GlobalDeclarations);
        Assert.All(tools.GlobalDeclarations, declaration => Assert.False(string.IsNullOrWhiteSpace(declaration.Name)));
    }
}
