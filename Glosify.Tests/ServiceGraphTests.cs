using Glosify.Services.Ai.Assistant;
using Glosify.Services.Classrooms;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Glosify.Tests;

/// <summary>
/// Resolves the services whose registration changed when the classroom service and the
/// assistant tools were split apart.
/// </summary>
/// <remarks>
/// A missing or mis-scoped registration in either would not fail a build and would not
/// fail a page render either — it would surface as a 500 the first time a user opened a
/// classroom or sent an assistant message. This resolves them from a real request scope
/// instead, which is where the scoped DbContext they all share lives.
/// </remarks>
public sealed class ServiceGraphTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ServiceGraphTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData(typeof(IClassroomAccess))]
    [InlineData(typeof(IClassroomDirectory))]
    [InlineData(typeof(IClassroomRoster))]
    [InlineData(typeof(IClassroomLibrary))]
    [InlineData(typeof(IClassroomConversation))]
    [InlineData(typeof(IClassroomPlanner))]
    [InlineData(typeof(IClassroomResults))]
    [InlineData(typeof(IClassroomCall))]
    [InlineData(typeof(IClassroomCallPresence))]
    [InlineData(typeof(IAssistantTools))]
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
