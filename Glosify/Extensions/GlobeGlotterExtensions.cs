using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;

namespace Glosify.Extensions;

public static class GlobeGlotterExtensions
{
    public static IServiceCollection AddGlobeGlotterSharedAuth(this IServiceCollection services,
        IConfiguration configuration, IWebHostEnvironment environment)
    {
        if (!configuration.GetValue<bool>("SharedAuth:Enabled")) return services;
        if (!environment.IsDevelopment() && !configuration.GetValue<bool>("GlobeGlotter:CanonicalEnabled"))
            throw new InvalidOperationException("Shared authentication requires the GlobeGlotter domain cutover.");
        var protection = services.AddDataProtection().SetApplicationName("GlobeGlotter.SharedAuth");
        if (environment.IsDevelopment())
        {
            protection.PersistKeysToFileSystem(new DirectoryInfo(configuration["SharedAuth:LocalKeyPath"]
                ?? throw new InvalidOperationException("SharedAuth:LocalKeyPath is required in development.")));
        }
        else
        {
            var credential = new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);
            protection.PersistKeysToAzureBlobStorage(new Uri(configuration["SharedAuth:BlobUri"]
                ?? throw new InvalidOperationException("SharedAuth:BlobUri is required.")), credential)
                .ProtectKeysWithAzureKeyVault(new Uri(configuration["SharedAuth:KeyIdentifier"]
                ?? throw new InvalidOperationException("SharedAuth:KeyIdentifier is required.")), credential);
        }
        return services;
    }

    public static IApplicationBuilder UseGlobeGlotterDomains(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        var migrated = configuration.GetValue<bool>("GlobeGlotter:CanonicalEnabled");
        var host = context.Request.Host.Host.ToLowerInvariant();
        var globe = host is "globeglotter.app" or "www.globeglotter.app";
        var old = host is "glosify.se" or "www.glosify.se";
        var path = context.Request.Path;
        // Preserve integrations and in-flight callbacks; never redirect a legacy POST.
        var compatibility = path.StartsWithSegments("/api") || path.StartsWithSegments("/extension")
            || path.StartsWithSegments("/ExtensionAuth") || path.StartsWithSegments("/signin-google")
            || path.StartsWithSegments("/signin-microsoft") || path.StartsWithSegments("/Account/ExternalLoginCallback")
            || path.StartsWithSegments("/Stripe") || path.StartsWithSegments("/webhooks")
            || path.StartsWithSegments("/deployment-version") || path.StartsWithSegments("/readyz")
            || path.StartsWithSegments("/health");
        if ((!migrated && globe) || (migrated && (host == "www.globeglotter.app"
            || (old && !compatibility && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))))))
        {
            var origin = migrated ? "https://globeglotter.app" : "https://glosify.se";
            context.Response.Redirect(origin + context.Request.PathBase + path + context.Request.QueryString,
                permanent: migrated, preserveMethod: true);
            return;
        }
        // Old-origin OAuth/bootstrap traffic may finish, but must never emit a parent-domain cookie.
        if (old && configuration.GetValue<bool>("SharedAuth:Enabled"))
            context.Response.OnStarting(() =>
            {
                var cookies = context.Response.Headers.SetCookie;
                context.Response.Headers.SetCookie = new Microsoft.Extensions.Primitives.StringValues(cookies
                    .Where(cookie => cookie is not null && !cookie.StartsWith(".GlobeGlotter.Auth", StringComparison.Ordinal))
                    .ToArray());
                return Task.CompletedTask;
            });
        await next();
    });
}
