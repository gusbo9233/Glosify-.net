using System.Threading.RateLimiting;

namespace Glosify.Extensions;

public static class RateLimitingExtensions
{
    /// <summary>
    /// Dedicated endpoint policies plus shared authenticated mutation limits.
    /// </summary>
    public static IServiceCollection AddGlosifyRateLimiting(this IServiceCollection services)
    {
        // Credential POSTs and OAuth flows use IP limits. Merely rendering a
        // password login page does not consume the credential limit.
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var path = context.Request.Path;

                var isRegistration = HttpMethods.IsPost(context.Request.Method)
                    && (string.Equals(path.Value?.TrimEnd('/'), "/Account/Register", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(path.Value?.TrimEnd('/'), "/api/auth/register", StringComparison.OrdinalIgnoreCase));
                if (isRegistration)
                {
                    var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter($"register:{ip}", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromHours(1),
                        QueueLimit = 0,
                    });
                }

                var isAuthPath = path.StartsWithSegments("/login")
                    || path.StartsWithSegments("/Account") && !path.StartsWithSegments("/account/usage")
                    || path.StartsWithSegments("/api/auth")
                    || path.StartsWithSegments("/api/extension-auth")
                    || path.StartsWithSegments("/Identity/Account");
                var isOAuth = path.Value?.Contains("ExternalLogin", StringComparison.OrdinalIgnoreCase) == true
                    || path.StartsWithSegments("/api/auth/external") || IsOAuthProtocolCallback(path);
                if (isOAuth || isAuthPath && HttpMethods.IsPost(context.Request.Method))
                {
                    var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter($"auth:{ip}", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 20,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    });
                }

                var pathSegments = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var isQuizAssistantPath = pathSegments is { Length: >= 3 }
                    && string.Equals(pathSegments[0], "Quiz", StringComparison.OrdinalIgnoreCase)
                    && Guid.TryParse(pathSegments[1], out _)
                    && string.Equals(pathSegments[2], "Assistant", StringComparison.OrdinalIgnoreCase);
                var isAssistantPath = path.StartsWithSegments("/Assistant")
                    || path.StartsWithSegments("/api/assistant")
                    || isQuizAssistantPath;
                if (isAssistantPath)
                {
                    // Default and endpoint-specific authentication run before this
                    // limiter; fall back to IP only for unauthenticated callers.
                    var caller = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                        ?? context.Connection.RemoteIpAddress?.ToString()
                        ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter($"ai:{caller}", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 60,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    });
                }

                if (string.Equals(
                    path.Value?.TrimEnd('/'),
                    "/Quiz/RepairJsonImportWithAi",
                    StringComparison.OrdinalIgnoreCase))
                {
                    var caller = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                        ?? context.Connection.RemoteIpAddress?.ToString()
                        ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter($"json-import-repair:{caller}", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 12,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    });
                }

                if (path.StartsWithSegments("/api/tts"))
                {
                    var caller = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                        ?? context.Connection.RemoteIpAddress?.ToString()
                        ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter($"tts:{caller}", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 60,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    });
                }

                var isBookUpload = HttpMethods.IsPost(context.Request.Method)
                    && (path.StartsWithSegments("/Books/Upload")
                        || string.Equals(
                            path.Value?.TrimEnd('/'),
                            "/api/books",
                            StringComparison.OrdinalIgnoreCase));
                if (isBookUpload)
                {
                    var caller = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                        ?? context.Connection.RemoteIpAddress?.ToString()
                        ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter($"book-upload:{caller}", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 3,
                        Window = TimeSpan.FromMinutes(10),
                        QueueLimit = 0,
                    });
                }

                var isBookTranslationPath = path.StartsWithSegments("/Books")
                    && (path.Value?.Contains("/Translation", StringComparison.OrdinalIgnoreCase) ?? false);
                if (isBookTranslationPath)
                {
                    var caller = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                        ?? context.Connection.RemoteIpAddress?.ToString()
                        ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter($"book-translation:{caller}", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 12,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    });
                }

                if (path.StartsWithSegments("/api/realtime-translation"))
                {
                    var caller = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                        ?? context.Connection.RemoteIpAddress?.ToString()
                        ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter($"realtime-translation:{caller}", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 90,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    });
                }

                if (HttpMethods.IsPost(context.Request.Method)
                    && string.Equals(
                        path.Value?.TrimEnd('/'),
                        "/api/translator/translate",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var caller = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                        ?? context.Connection.RemoteIpAddress?.ToString()
                        ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter($"text-translation:{caller}", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    });
                }

                if (HttpMethods.IsPost(context.Request.Method)
                    && string.Equals(
                        path.Value?.TrimEnd('/'),
                        "/api/translator/saved-translations",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var caller = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                        ?? context.Connection.RemoteIpAddress?.ToString()
                        ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter($"saved-translation:{caller}", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    });
                }

                if (HttpMethods.IsPost(context.Request.Method)
                    && string.Equals(
                        path.Value?.TrimEnd('/'),
                        "/Payments/CreateCheckoutSession",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var caller = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                        ?? context.Connection.RemoteIpAddress?.ToString()
                        ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter($"stripe-checkout:{caller}", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(10),
                        QueueLimit = 0,
                    });
                }

                // The demo link is meant to end up on a CV, so it will be crawled. Each wrong
                // code costs an attacker a request against this window, which makes guessing
                // the code impractical without affecting an employer who opens the link twice.
                if (string.Equals(path.Value, "/demo", StringComparison.OrdinalIgnoreCase))
                {
                    var visitor = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter($"demo:{visitor}", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(5),
                        QueueLimit = 0,
                    });
                }

                return RateLimitPartition.GetNoLimiter("default");
            });
            var mutations = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var path = context.Request.Path.Value ?? "";
                if (context.User.Identity?.IsAuthenticated != true || !IsMutation(context.Request.Method)
                    || path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/Account", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("/account/usage", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("webhook", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("RealtimeTranslation", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("realtime-translation", StringComparison.OrdinalIgnoreCase))
                    return RateLimitPartition.GetNoLimiter("other");
                var user = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(user, _ => new FixedWindowRateLimiterOptions
                    { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
            });
            var heavy = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var path = context.Request.Path.Value ?? "";
                if (context.User.Identity?.IsAuthenticated != true || !IsMutation(context.Request.Method)
                    || !(path.Contains("import", StringComparison.OrdinalIgnoreCase) || path.Contains("copy", StringComparison.OrdinalIgnoreCase)))
                    return RateLimitPartition.GetNoLimiter("other");
                var user = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(user, _ => new FixedWindowRateLimiterOptions
                    { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
            });
            var concurrency = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var path = context.Request.Path.Value?.TrimEnd('/') ?? "";
                var pdf = path.Equals("/Books/Upload", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/books", StringComparison.OrdinalIgnoreCase);
                var tts = path.Equals("/api/tts", StringComparison.OrdinalIgnoreCase);
                if (!HttpMethods.IsPost(context.Request.Method) || !(pdf || tts) || context.User.Identity?.IsAuthenticated != true)
                    return RateLimitPartition.GetNoLimiter("other");
                var user = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "unknown";
                return RateLimitPartition.GetConcurrencyLimiter((pdf ? "pdf:" : "tts:") + user,
                    _ => new ConcurrencyLimiterOptions { PermitLimit = pdf ? 1 : 2, QueueLimit = 0 });
            });
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(options.GlobalLimiter, mutations, heavy, concurrency);
            options.OnRejected = async (rejected, ct) =>
            {
                Glosify.Services.Abuse.AbuseMetrics.RecordQuota("request_throttle", false);
                var retry = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var duration) ? duration : TimeSpan.FromSeconds(1);
                rejected.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (long)Math.Ceiling(retry.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                await Glosify.Infrastructure.Api.GlosifyProblemDetails.WriteAsync(rejected.HttpContext, 429, "rate_limited", "Too many requests. Please try again later.");
            };
        });
        return services;
    }

    private static bool IsMutation(string method) => HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
        || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

    public static bool IsOAuthProtocolCallback(PathString path) => path.StartsWithSegments("/signin-google")
        || path.StartsWithSegments("/signin-microsoft");
}
