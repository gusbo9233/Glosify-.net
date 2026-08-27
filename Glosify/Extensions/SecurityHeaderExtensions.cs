namespace Glosify.Extensions;

public static class SecurityHeaderExtensions
{
    /// <summary>
    /// Writes the security headers on every response. App scripts live in wwwroot/js and
    /// behaviors use data-* attributes instead of inline on* handlers; the only external
    /// scripts are self-hosted. 'unsafe-inline' for styles remains because views use style
    /// attributes, while fonts.googleapis.com/gstatic.com serve the web fonts linked by the
    /// layout.
    /// </summary>
    public static IApplicationBuilder UseGlosifySecurityHeaders(
        this IApplicationBuilder app,
        IConfiguration configuration)
    {
        var configuredFormActionOrigins = configuration
            .GetSection("Security:Csp:FormActionOrigins")
            .Get<string[]>() ?? [];
        var contentSecurityPolicy = BuildContentSecurityPolicy(
            configuredFormActionOrigins);

        return app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Content-Security-Policy"] = contentSecurityPolicy;
            headers["Permissions-Policy"] =
                "microphone=(), camera=(), geolocation=(), payment=(), usb=(), interest-cohort=()";
            await next();
        });
    }

    private static string BuildContentSecurityPolicy(IEnumerable<string> formActionOrigins)
    {
        var allowedFormActionSources = formActionOrigins
            .Select(NormalizeCspOrigin)
            .Where(origin => !string.IsNullOrWhiteSpace(origin))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var formActionDirective = string.Join(' ', ["'self'", .. allowedFormActionSources]);

        return
            "default-src 'self'; " +
            "script-src 'self'; " +
            "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
            "font-src 'self' https://fonts.gstatic.com; " +
            "img-src 'self' data: blob:; " +
            // tts.js renders server-generated audio through object URLs.
            "media-src 'self' blob:; " +
            "connect-src 'self'; " +
            "worker-src 'self'; " +
            "frame-ancestors 'none'; " +
            "base-uri 'self'; " +
            $"form-action {formActionDirective}";
    }

    private static string? NormalizeCspOrigin(string? origin)
    {
        if (!Uri.TryCreate(origin?.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }
}
