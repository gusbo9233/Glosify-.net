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
        Assert.Equal("https://globeglotter.app/privacy/english?source=old&__gg=20261006", response.Headers.Location?.AbsoluteUri);
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

    [Fact]
    public async Task CachedPreCutoverRedirectCanReachNewOriginWithoutLooping()
    {
        using var factory = _factory.WithWebHostBuilder(builder => builder.UseSetting("GlobeGlotter:CanonicalEnabled", "true"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var cachedSource = new Uri("https://globeglotter.app/privacy/english");
        // Emulate a browser that already cached the old 308 for this exact URL.
        var cachedDestination = new Uri("https://glosify.se/privacy/english");
        var response = await client.GetAsync(cachedDestination);
        Assert.NotEqual(cachedSource, response.Headers.Location);
        Assert.Equal("globeglotter.app", response.Headers.Location!.Host);
        var recovered = await client.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Null(recovered.Headers.Location);
    }

    [Theory]
    [InlineData("POST", "/login")]
    [InlineData("POST", "/Account/Login")]
    [InlineData("POST", "/Account/ExternalLogin")]
    [InlineData("POST", "/Identity/Account/LoginWith2fa")]
    [InlineData("GET", "/signin-google?code=old-code&state=old-state")]
    [InlineData("GET", "/signin-microsoft?code=old-code")]
    [InlineData("GET", "/Account/ExternalLoginCallback")]
    public async Task OldOriginSignInRestartsBeforeProcessingCredentials(string method, string path)
    {
        using var factory = _factory.WithWebHostBuilder(builder => builder
            .UseSetting("GlobeGlotter:CanonicalEnabled", "true")
            .UseSetting("SharedAuth:Enabled", "true")
            .UseSetting("SharedAuth:LocalKeyPath", Path.Combine(Path.GetTempPath(), "globe-redirect-test")));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://glosify.se") });
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("https://globeglotter.app/login?__gg=20261006", response.Headers.Location?.AbsoluteUri);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    private HttpClient CreateClient(string host) => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        BaseAddress = new Uri($"https://{host}"),
    });
}
