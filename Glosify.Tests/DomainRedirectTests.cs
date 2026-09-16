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
    public async Task GlobeGlotterHostsPermanentlyRedirectToGlosify(string host)
    {
        using var client = CreateClient(host);

        var response = await client.GetAsync("/sv/terms?source=domain%20preview&mode=1");

        Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
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

    private HttpClient CreateClient(string host) => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        BaseAddress = new Uri($"https://{host}"),
    });
}
