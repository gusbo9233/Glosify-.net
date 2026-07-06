using Glosify.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Glosify.Models;
using Glosify.Services;
using Glosify.Services.Quizzes;
using Glosify.Services.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Threading.RateLimiting;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Llm;
using Glosify.Services.Flashcards;
using Glosify.Services.Language;
using Glosify.Services.Typing;
using Glosify.Services.Words;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
});

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    var adminEmails = builder.Configuration.GetSection("Admin:Emails").Get<string[]>() ?? [];
    options.AddPolicy("AiCreditAdmin", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context =>
        {
            var email = context.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                ?? context.User.Identity?.Name
                ?? string.Empty;
            return adminEmails.Any(adminEmail => string.Equals(
                adminEmail,
                email,
                StringComparison.OrdinalIgnoreCase));
        });
    });
});
builder.Services.AddMemoryCache();

// Rate limiting: strict on credential endpoints (per IP), moderate on the AI
// assistant (per user), unlimited elsewhere. Counts only POSTs on auth paths so
// rendering the login page never trips the limiter.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var path = context.Request.Path;

        var isAuthPath = path.StartsWithSegments("/login")
            || path.StartsWithSegments("/Account")
            || path.StartsWithSegments("/api/auth")
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

        var isAssistantPath = path.StartsWithSegments("/Assistant")
            || (path.Value?.Contains("/Assistant", StringComparison.OrdinalIgnoreCase) ?? false);
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

        return RateLimitPartition.GetNoLimiter("default");
    });
});

// Add Identity
builder.Services.AddDefaultIdentity<ApplicationUser>(options =>
{
    // Off by default because registration has no email-confirmation flow yet;
    // a deployment that adds an IEmailSender can flip this without a code change.
    options.SignIn.RequireConfirmedAccount =
        builder.Configuration.GetValue("Identity:RequireConfirmedAccount", false);
})
    .AddEntityFrameworkStores<GlosifyContext>()
    .AddDefaultTokenProviders()
    // Enables MapIdentityApi (token-based register/login/refresh) for the mobile app,
    // backed by the same user store as the cookie-based web sign-in.
    .AddApiEndpoints();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/login";
});

var authenticationBuilder = builder.Services.AddAuthentication();
authenticationBuilder.AddBearerToken(IdentityConstants.BearerScheme);
var googleClientId = builder.Configuration["Authentication:Google:ClientId"];
var googleClientSecret = builder.Configuration["Authentication:Google:ClientSecret"];
var microsoftClientId = builder.Configuration["Authentication:Microsoft:ClientId"];
var microsoftClientSecret = builder.Configuration["Authentication:Microsoft:ClientSecret"];
if (!string.IsNullOrWhiteSpace(googleClientId) && !string.IsNullOrWhiteSpace(googleClientSecret))
{
    authenticationBuilder.AddGoogle(options =>
    {
        options.ClientId = googleClientId;
        options.ClientSecret = googleClientSecret;
        options.Events.OnRemoteFailure = context =>
        {
            var logger = context.HttpContext.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("GoogleAuthentication");
            logger.LogWarning(context.Failure, "Google external login failed.");

            context.HandleResponse();
            context.Response.Redirect("/login?externalLoginError=Google");
            return Task.CompletedTask;
        };
    });
}

if (!string.IsNullOrWhiteSpace(microsoftClientId) && !string.IsNullOrWhiteSpace(microsoftClientSecret))
{
    authenticationBuilder.AddMicrosoftAccount(options =>
    {
        options.ClientId = microsoftClientId;
        options.ClientSecret = microsoftClientSecret;
        options.Events.OnRemoteFailure = context =>
        {
            var logger = context.HttpContext.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("MicrosoftAuthentication");
            logger.LogWarning(context.Failure, "Microsoft external login failed.");

            context.HandleResponse();
            context.Response.Redirect("/login?externalLoginError=Microsoft");
            return Task.CompletedTask;
        };
    });
}

builder.Services.AddRazorPages();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ILanguageContext, CookieLanguageContext>();

builder.Services.Configure<GeminiOptions>(builder.Configuration.GetSection("Gemini"));
builder.Services.Configure<AiUsageOptions>(builder.Configuration.GetSection("AiUsage"));
builder.Services.Configure<GeminiOptions>(options =>
{
    var apiKey = builder.Configuration["GEMINI_API_KEY"];
    if (!string.IsNullOrWhiteSpace(apiKey))
    {
        options.ApiKey = apiKey;
    }

    var structuredModel = builder.Configuration["GEMINI_STRUCTURED_MODEL"];
    if (!string.IsNullOrWhiteSpace(structuredModel))
    {
        options.StructuredModel = structuredModel;
    }

    var assistantModel = builder.Configuration["GEMINI_ASSISTANT_MODEL"];
    if (!string.IsNullOrWhiteSpace(assistantModel))
    {
        options.AssistantModel = assistantModel;
    }

    var visionModel = builder.Configuration["GEMINI_VISION_MODEL"];
    if (!string.IsNullOrWhiteSpace(visionModel))
    {
        options.VisionModel = visionModel;
    }

    var timeoutSeconds = builder.Configuration["GEMINI_TIMEOUT_SECONDS"];
    if (int.TryParse(timeoutSeconds, out var parsedTimeoutSeconds) && parsedTimeoutSeconds > 0)
    {
        options.TimeoutSeconds = parsedTimeoutSeconds;
    }
});

// Register application services
builder.Services.Configure<BlobStorageOptions>(builder.Configuration.GetSection("BlobStorage"));
builder.Services.AddScoped<IQuizService, QuizService>();
builder.Services.AddScoped<ICollectionService, CollectionService>();
builder.Services.AddScoped<IQuizRepairService, QuizRepairService>();
builder.Services.AddScoped<IWordService, WordService>();
builder.Services.AddSingleton<IQuizSessionRegistry, QuizSessionRegistry>();
builder.Services.AddScoped<IFlashcardSessionService, FlashcardSessionService>();
builder.Services.AddScoped<ITypingQuizService, TypingQuizService>();
builder.Services.AddScoped<ITypingSessionService, TypingSessionService>();
builder.Services.AddSingleton<IBookFileStorage, AzureBlobBookFileStorage>();
builder.Services.AddScoped<IPdfTextExtractionService, PdfPigTextExtractionService>();
builder.Services.AddScoped<IBookDocumentService, BookDocumentService>();
builder.Services.AddScoped<IAiCreditService, AiCreditService>();
// The model factory is a singleton so the GoogleAI client and configured models are
// created once; GeminiClient stays scoped because it charges the per-request credit service.
builder.Services.AddSingleton<IGeminiModelFactory, GeminiModelFactory>();
builder.Services.AddScoped<IGeminiClient, GeminiClient>();
builder.Services.AddScoped<IVocabularyGenerationService, LlmVocabularyGenerationService>();
builder.Services.AddScoped<IImageTextExtractionService, LlmImageTextExtractionService>();
builder.Services.AddScoped<IAssistantTools, AssistantTools>();
builder.Services.AddScoped<IChangeApplier, ChangeApplier>();
builder.Services.AddScoped<IAssistantOrchestrator, AssistantOrchestrator>();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Connection string 'DefaultConnection' is not configured. Set ConnectionStrings__DefaultConnection in the host environment or appsettings.Development.json for local development.");
}
var sqlConnectionString = BuildColdStartFriendlyConnectionString(connectionString);

// Configure SQL Server database
builder.Services.AddDbContext<GlosifyContext>(options =>
    options.UseSqlServer(
        sqlConnectionString,
        sqlOptions =>
        {
            // Azure SQL serverless cold-starts can take 60s+; first query after auto-pause must wait it out.
            sqlOptions.CommandTimeout(120);
            sqlOptions.EnableRetryOnFailure(
                maxRetryCount: 10,
                maxRetryDelay: TimeSpan.FromSeconds(30),
                errorNumbersToAdd: null);
        }
    )
    .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
);

var app = builder.Build();

// Azure App Service front ends terminate TLS and forward the client address in
// X-Forwarded-* headers; without this, RemoteIpAddress is the front end's address
// and every user shares the same rate-limit partition.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost,
};
// The App Service front-end addresses are not statically known, so the default
// loopback-only proxy allowlist must be cleared for the headers to be honored.
forwardedHeadersOptions.KnownNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

// Configure the HTTP request pipeline.
app.UseExceptionHandler("/Home/Error");
if (!app.Environment.IsDevelopment())
{
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

// Security headers on every response. CSP notes: scripts are same-origin only —
// all view scripts live in wwwroot/js and behaviors use data-* attributes instead
// of inline on* handlers; 'unsafe-inline' for styles remains because views use
// style attributes; fonts.googleapis.com/gstatic.com serve the web fonts the
// layout links; everything else is same-origin only.
var configuredFormActionOrigins = builder.Configuration
    .GetSection("Security:Csp:FormActionOrigins")
    .Get<string[]>() ?? [];
var contentSecurityPolicy = BuildContentSecurityPolicy(configuredFormActionOrigins);
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["Content-Security-Policy"] = contentSecurityPolicy;
    await next();
});

app.UseRouting();

// Authentication must run before the rate limiter so the assistant limit can be
// partitioned per user rather than per IP.
app.UseAuthentication();

app.UseRateLimiter();

app.UseAuthorization();

app.MapStaticAssets().AllowAnonymous();

app.MapControllerRoute(
    name: "login",
    pattern: "login",
    defaults: new { controller = "Account", action = "Login" });

app.MapControllerRoute(
    name: "quizzes",
    pattern: "Quizzes/{action=Index}/{id?}",
    defaults: new { controller = "Quiz" });

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.MapRazorPages().AllowAnonymous();

// Token auth endpoints for the mobile app (/api/auth/login, /register, /refresh, ...).
// AllowAnonymous is required because of the fallback authorization policy; the /manage
// endpoints in the group resolve the user from the bearer token and 404 without one.
app.MapGroup("/api/auth").MapIdentityApi<ApplicationUser>().AllowAnonymous();


app.Run();
return 0;

static string BuildColdStartFriendlyConnectionString(string connectionString)
{
    var builder = new SqlConnectionStringBuilder(connectionString);
    if (builder.ConnectTimeout < 120)
    {
        builder.ConnectTimeout = 120;
    }

    return builder.ConnectionString;
}

static string BuildContentSecurityPolicy(IEnumerable<string> formActionOrigins)
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
        "img-src 'self' data:; " +
        "connect-src 'self'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        $"form-action {formActionDirective}";
}

static string? NormalizeCspOrigin(string? origin)
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

public partial class Program { }
