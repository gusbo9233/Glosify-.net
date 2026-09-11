using System.Net;
using Glosify.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Glosify.Tests;

public sealed class ForwardedClientAddressTests
{
    [Theory]
    [InlineData(false, false, "10.0.0.2")]
    [InlineData(false, true, "203.0.113.8")]
    [InlineData(true, false, "203.0.113.8")]
    public async Task OnlyTheTrustedBoundaryCanForwardOneClientHop(bool appService, bool trustedProxy, string expected)
    {
        var settings = new Dictionary<string, string?> { ["AllowedHosts"] = "app.example" };
        if (appService) settings["WEBSITE_INSTANCE_ID"] = "test-instance";
        if (trustedProxy) settings["ForwardedHeaders:KnownProxies:0"] = "10.0.0.2";
        var context = await Forward(settings, "10.0.0.2", "198.51.100.99, 203.0.113.8");
        Assert.Equal(IPAddress.Parse(expected), context.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task ForwardedHostMustMatchTheApplicationsHostAllowlist()
    {
        var context = await Forward(new() { ["WEBSITE_INSTANCE_ID"] = "test-instance", ["AllowedHosts"] = "app.example" },
            "10.0.0.2", "203.0.113.8", "attacker.example");
        Assert.Equal("app.example", context.Request.Host.Value);
    }

    private static async Task<DefaultHttpContext> Forward(Dictionary<string, string?> settings, string remote, string forwarded, string? host = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseForwardedHeaders(TrustedForwarding.Create(configuration));
        app.Run(_ => Task.CompletedTask);
        var context = new DefaultHttpContext { RequestServices = services };
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        context.Request.Host = new HostString("app.example");
        context.Request.Headers["X-Forwarded-For"] = forwarded;
        if (host is not null) context.Request.Headers["X-Forwarded-Host"] = host;
        await app.Build()(context);
        return context;
    }
}
