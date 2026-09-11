using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Glosify.Infrastructure;

public static class TrustedForwarding
{
    public static ForwardedHeadersOptions Create(IConfiguration configuration)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardLimit = 1,
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
        };
        // App Service's front end is the forwarding boundary; its addresses are
        // dynamic. Other hosts retain loopback plus explicitly trusted proxies.
        if (!string.IsNullOrWhiteSpace(configuration["WEBSITE_INSTANCE_ID"]))
        {
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        }
        else
        {
            foreach (var proxy in configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
                options.KnownProxies.Add(IPAddress.Parse(proxy));
        }
        foreach (var host in (configuration["AllowedHosts"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            options.AllowedHosts.Add(host);
        return options;
    }
}
