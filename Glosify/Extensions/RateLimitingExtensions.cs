using System.Threading.RateLimiting;
using Glosify.Services.Speaking;

namespace Glosify.Extensions;

public static class RateLimitingExtensions
{
    /// <summary>
    /// Per-endpoint rate limits. Everything not named here is unlimited.
    /// </summary>
    public static IServiceCollection AddGlosifyRateLimiting(this IServiceCollection services)
    {
        // Rate limiting: strict on credential endpoints (per IP), moderate on the AI
        // assistant (per user), unlimited elsewhere. Counts only POSTs on auth paths so
        // rendering the login page never trips the limiter.
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                if (context.HttpContext.Request.Path.StartsWithSegments("/api/speaking"))
                {
                    SpeakingTelemetry.RateLimits.Add(1);
                }

                return ValueTask.CompletedTask;
            };
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
                    || path.StartsWithSegments("/Account")
                    || path.StartsWithSegments("/api/auth")
                    || path.StartsWithSegments("/api/extension-auth")
                    || path.StartsWithSegments("/Identity/Account");
                if (isAuthPath && HttpMethods.IsPost(context.Request.Method))
                {
                    var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter($"auth:{ip}", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 20,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    });
                }

                // Must precede the assistant rule, which would otherwise match this path.
                // Microsoft Foundry calls the MCP endpoint as one shared identity with no user
                // claim, so partitioning on the caller address would drop every user's tool calls
                // into a single bucket. The signed session token in the route is per response and
                // names the acting user, so it isolates them; the limit then bounds how many tool
                // calls one agent response can make rather than how many any one user can.
                if (path.StartsWithSegments("/assistant/mcp", out _, out var mcpRemainder))
                {
                    var session = mcpRemainder.Value?.Trim('/');
                    var partition = string.IsNullOrEmpty(session)
                        ? $"mcp-anon:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}"
                        : $"mcp:{session}";
                    return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 120,
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
                    // UseRateLimiter runs after UseAuthentication, so the user id claim is
                    // available here; fall back to IP only for unauthenticated callers.
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

                if (string.Equals(
                    path.Value,
                    "/api/speaking/speech-token",
                    StringComparison.OrdinalIgnoreCase))
                {
                    var caller = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                        ?? context.Connection.RemoteIpAddress?.ToString()
                        ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter($"speaking-token:{caller}", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 12,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    });
                }

                if (path.StartsWithSegments("/api/speaking"))
                {
                    var caller = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                        ?? context.Connection.RemoteIpAddress?.ToString()
                        ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter($"speaking:{caller}", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
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

                if (path.StartsWithSegments("/Classroom") && HttpMethods.IsPost(context.Request.Method))
                {
                    var member = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                        ?? context.Connection.RemoteIpAddress?.ToString()
                        ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter($"classroom:{member}", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1),
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
        });
        return services;
    }
}
