namespace Glosify.Extensions;

public static class SecurityHeaderExtensions
{
    /// <summary>
    /// Writes the security headers on every response. App scripts live in wwwroot/js and
    /// behaviors use data-* attributes instead of inline on* handlers; the only external
    /// script origin is jsDelivr for the pinned Three.js module. 'unsafe-inline' for styles
    /// remains because views use style attributes, while fonts.googleapis.com/gstatic.com
    /// serve the web fonts linked by the layout.
    /// </summary>
    public static IApplicationBuilder UseGlosifySecurityHeaders(
        this IApplicationBuilder app,
        IConfiguration configuration)
    {
        var configuredFormActionOrigins = configuration
            .GetSection("Security:Csp:FormActionOrigins")
            .Get<string[]>() ?? [];
        // Extra connect-src entries for the ACS calling SDK (video signaling/media
        // endpoints); configurable so new ACS domains don't require a code change.
        var configuredConnectSources = configuration
            .GetSection("Security:Csp:ConnectSources")
            .Get<string[]>() ?? [];
        configuredConnectSources =
        [
            .. configuredConnectSources,
            .. BuildSpeechConnectSources(
                configuration["Speech:Region"],
                configuration["Speech:Endpoint"]),
        ];
        var contentSecurityPolicy = BuildContentSecurityPolicy(
            configuredFormActionOrigins,
            configuredConnectSources);

        return app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Content-Security-Policy"] = contentSecurityPolicy;
            // Speaking and the classroom call need the microphone and camera on this origin;
            // everything else is denied outright rather than left to the browser default.
            headers["Permissions-Policy"] =
                "microphone=(self), camera=(self), geolocation=(), payment=(), usb=(), interest-cohort=()";
            await next();
        });
    }

    private static string BuildContentSecurityPolicy(IEnumerable<string> formActionOrigins, IEnumerable<string> extraConnectSources)
    {
        var allowedFormActionSources = formActionOrigins
            .Select(NormalizeCspOrigin)
            .Where(origin => !string.IsNullOrWhiteSpace(origin))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var formActionDirective = string.Join(' ', ["'self'", .. allowedFormActionSources]);

        // Wildcard hosts (e.g. https://*.communication.azure.com) are valid CSP
        // sources but not absolute URIs, so they bypass NormalizeCspOrigin and are
        // sanitized to a conservative character set instead.
        var connectSources = extraConnectSources
            .Select(source => source?.Trim())
            .Where(source => !string.IsNullOrWhiteSpace(source)
                && source.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or ':' or '/' or '*'))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var connectDirective = string.Join(' ', ["'self'", .. connectSources]);

        return
            "default-src 'self'; " +
            "script-src 'self' https://cdn.jsdelivr.net; " +
            "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
            "font-src 'self' https://fonts.gstatic.com; " +
            "img-src 'self' data: blob:; " +
            // The browser Speech SDK renders synthesized replies through a blob:
            // URL, while the server-side fallback streams same-origin MP3 audio.
            "media-src 'self' blob:; " +
            $"connect-src {connectDirective}; " +
            // The ACS calling SDK spins up blob: web workers for media handling.
            "worker-src 'self' blob:; " +
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

    private static IEnumerable<string> BuildSpeechConnectSources(string? region, string? endpoint)
    {
        if (!string.IsNullOrWhiteSpace(endpoint)
            && Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var endpointUri)
            && endpointUri.Scheme == Uri.UriSchemeHttps)
        {
            yield return endpointUri.GetLeftPart(UriPartial.Authority);
        }

        var normalizedRegion = region?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedRegion)
            || normalizedRegion.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            yield break;
        }

        yield return $"https://{normalizedRegion}.api.cognitive.microsoft.com";
        yield return $"https://{normalizedRegion}.stt.speech.microsoft.com";
        yield return $"wss://{normalizedRegion}.stt.speech.microsoft.com";
        yield return $"https://{normalizedRegion}.tts.speech.microsoft.com";
        yield return $"wss://{normalizedRegion}.tts.speech.microsoft.com";
    }
}
