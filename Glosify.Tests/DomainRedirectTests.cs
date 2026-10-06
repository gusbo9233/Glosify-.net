using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Glosify.Tests;

public sealed class DomainRedirectTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DomainRedirectTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("globeglotter.app")]
    [InlineData("www.globeglotter.app")]
    public async Task GlobeGlotterHostsTemporarilyRedirectBeforeCutover(string host)
    {
        using var client = CreateClient(host);

        var response = await client.GetAsync("/sv/terms?source=domain%20preview&mode=1");

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal(
            "https://glosify.se/sv/terms?source=domain%20preview&mode=1",
            response.Headers.Location?.AbsoluteUri);
    }

    [Theory]
    [InlineData("glosify.se")]
    [InlineData("www.glosify.se")]
    [InlineData("glosify-app.azurewebsites.net")]
    public async Task ExistingProductionHostsAreNotRedirected(string host)
    {
        using var client = CreateClient(host);

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task UnapprovedHostIsRejected()
    {
        using var client = CreateClient("example.invalid");

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("glosify.se")]
    [InlineData("www.glosify.se")]
    [InlineData("www.globeglotter.app")]
    public async Task CutoverRedirectsBrowserPagesToApex(string host)
    {
        using var factory = _factory.WithWebHostBuilder(builder => builder.UseSetting("GlobeGlotter:CanonicalEnabled", "true"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri($"https://{host}") });
        var response = await client.GetAsync("/privacy/english?source=old");
        Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
        Assert.Equal("https://globeglotter.app/privacy/english?source=old", response.Headers.Location?.AbsoluteUri);
    }

    [Theory]
    [InlineData("/api/missing")]
    [InlineData("/extension/missing")]
    [InlineData("/webhooks/missing")]
    public async Task CutoverPreservesLegacyIntegrationRoutes(string path)
    {
        using var factory = _factory.WithWebHostBuilder(builder => builder.UseSetting("GlobeGlotter:CanonicalEnabled", "true"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://glosify.se") });
        var response = await client.GetAsync(path);
        Assert.NotEqual(HttpStatusCode.PermanentRedirect, response.StatusCode);
        Assert.DoesNotContain("globeglotter.app", response.Headers.Location?.OriginalString ?? "");
        var post = await client.PostAsync("/Account/Login", new StringContent(""));
        Assert.NotEqual(HttpStatusCode.PermanentRedirect, post.StatusCode);
    }

    private HttpClient CreateClient(string host) => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        BaseAddress = new Uri($"https://{host}"),
    });
}
