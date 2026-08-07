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
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Threading.RateLimiting;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Assistant.Mcp;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Ai.Llm;
using Glosify.Services.Classrooms;
using Glosify.Services.CustomQuizzes;
using Glosify.Services.Flashcards;
using Glosify.Services.Language;
using Glosify.Services.Speech;
using Glosify.Services.Speaking;
using Glosify.Services.Typing;
using Glosify.Services.Words;
using Glosify.Services.Auth;
using Glosify.Services.RealtimeTranslation;
using Azure.Core;
using Azure.AI.Projects;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
});

// Kestrel serves the rendered views uncompressed and nothing in front of it
// compresses them, so an average page ships ~22 KB of HTML over the wire.
// Static assets are already compressed at build time by MapStaticAssets, and
// this middleware skips any response that arrives with a Content-Encoding.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});

builder.Services.AddSignalR();

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

// Liveness only, deliberately: it must answer while Azure SQL serverless is still
// resuming, and a dependency check here would report the app unhealthy through a
// cold start it is designed to wait out.
builder.Services.AddHealthChecks();

// Rate limiting: strict on credential endpoints (per IP), moderate on the AI
// assistant (per user), unlimited elsewhere. Counts only POSTs on auth paths so
// rendering the login page never trips the limiter.
builder.Services.AddRateLimiter(options =>
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
builder.Services.AddScoped<IQuizLanguagePreferenceService, QuizLanguagePreferenceService>();

builder.Services.AddOptions<GeminiOptions>()
    .Bind(builder.Configuration.GetSection("Gemini"));
builder.Services.AddOptions<GenerativeAiOptions>()
    .Bind(builder.Configuration.GetSection(GenerativeAiOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<GenerativeAiOptions>, GenerativeAiOptionsValidator>();
builder.Services.AddOptions<AiUsageOptions>()
    .Bind(builder.Configuration.GetSection("AiUsage"))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<AiUsageOptions>, AiUsageOptionsValidator>();
builder.Services.AddOptions<ExtensionAuthOptions>()
    .Bind(builder.Configuration.GetSection(ExtensionAuthOptions.SectionName));
builder.Services.AddOptions<DemoAccountOptions>()
    .Bind(builder.Configuration.GetSection(DemoAccountOptions.SectionName));
builder.Services.AddScoped<DemoAccountSeeder>();
builder.Services.AddOptions<RealtimeTranslationOptions>()
    .Bind(builder.Configuration.GetSection(RealtimeTranslationOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<RealtimeTranslationOptions>, RealtimeTranslationOptionsValidator>();
// Keep the legacy Gemini credential alias only while the explicit rollback
// provider is available. All model and timeout settings use standard ASP.NET
// double-underscore configuration binding.
builder.Services.Configure<GeminiOptions>(options =>
{
    var apiKey = builder.Configuration["GEMINI_API_KEY"];
    if (!string.IsNullOrWhiteSpace(apiKey))
    {
        options.ApiKey = apiKey;
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
builder.Services.AddSingleton<IBookPageTranslationCoordinator, BookPageTranslationCoordinator>();
builder.Services.AddScoped<IBookPageTranslationService, BookPageTranslationService>();
builder.Services.AddScoped<IClassroomService, ClassroomService>();
builder.Services.AddScoped<IQuizAttemptService, QuizAttemptService>();
builder.Services.AddScoped<ICustomQuizService, CustomQuizService>();
builder.Services.AddSingleton<ICustomQuizTemplateCatalog, CustomQuizTemplateCatalog>();
builder.Services.Configure<Glosify.Services.Communication.AcsOptions>(
    builder.Configuration.GetSection(Glosify.Services.Communication.AcsOptions.SectionName));
builder.Services.AddScoped<Glosify.Services.Communication.IAcsTokenService, Glosify.Services.Communication.AcsTokenService>();
builder.Services.AddScoped<IAiCreditService, AiCreditService>();
builder.Services.AddSingleton<IExtensionAuthorizationCodeStore, ExtensionAuthorizationCodeStore>();
builder.Services.AddSingleton<IRealtimeTranslationRelayTokenStore, RealtimeTranslationRelayTokenStore>();
builder.Services.AddSingleton<IFoundryTranslationRelay, FoundryTranslationRelay>();
builder.Services.AddScoped<IRealtimeTranslationService, RealtimeTranslationService>();
builder.Services.AddScoped<IRealtimeTranslationTranscriptService, RealtimeTranslationTranscriptService>();
builder.Services.AddHostedService<RealtimeTranslationCleanupService>();
// The Gemini model factory remains only for the explicit deployment-level
// rollback path during the Foundry soak.
builder.Services.AddSingleton<IGeminiModelFactory, GeminiModelFactory>();
builder.Services.AddSingleton<IGenerativeAiModelResolver, GenerativeAiModelResolver>();
builder.Services.AddSingleton<IFoundryAgentInvoker, FoundryAgentInvoker>();
builder.Services.AddScoped<FoundryGenerativeAiClient>();
builder.Services.AddScoped<GeminiGenerativeAiClient>();
builder.Services.AddScoped<IGenerativeAiClient>(services =>
{
    var provider = services.GetRequiredService<IOptions<GenerativeAiOptions>>()
        .Value.Provider.Trim();
    return string.Equals(
        provider,
        GenerativeAiOptions.GeminiProvider,
        StringComparison.OrdinalIgnoreCase)
        ? services.GetRequiredService<GeminiGenerativeAiClient>()
        : services.GetRequiredService<FoundryGenerativeAiClient>();
});
builder.Services.AddScoped<IVocabularyGenerationService, LlmVocabularyGenerationService>();
builder.Services.AddScoped<IImageTextExtractionService, LlmImageTextExtractionService>();
builder.Services.AddScoped<IAssistantTools, AssistantTools>();
builder.Services.AddScoped<IChangeApplier, ChangeApplier>();
builder.Services.AddScoped<IAssistantPendingChangeStore, AssistantPendingChangeStore>();
builder.Services.AddScoped<IAssistantOrchestrator, AssistantOrchestrator>();
builder.Services.AddAssistantMcp(builder.Configuration);

builder.Services.Configure<SpeechOptions>(builder.Configuration.GetSection(SpeechOptions.SectionName));
builder.Services.AddOptions<SpeakingOptions>()
    .Bind(builder.Configuration.GetSection(SpeakingOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<SpeakingOptions>, SpeakingOptionsValidator>();
builder.Services.AddSingleton<TokenCredential>(_ =>
    FoundryCredentialFactory.Create(builder.Environment, builder.Configuration));
builder.Services.AddSingleton(services =>
{
    var endpoint = services.GetRequiredService<IOptions<GenerativeAiOptions>>()
        .Value.Foundry.ProjectEndpoint;
    return new AIProjectClient(
        new Uri(endpoint, UriKind.Absolute),
        services.GetRequiredService<TokenCredential>());
});
builder.Services.AddHttpClient(nameof(AzureTextToSpeechService));
builder.Services.AddSingleton<ITextToSpeechService, AzureTextToSpeechService>();
builder.Services.AddSingleton<ISpeechAuthorizationTokenService, SpeechAuthorizationTokenService>();
builder.Services.AddSingleton<ISpeakingAgentClient, FoundrySpeakingAgentClient>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ISpeakingSessionStore, SpeakingSessionStore>();
builder.Services.AddScoped<ISpeakingService, SpeakingService>();
builder.Services.AddScoped<ISpeakingQuizReader, SpeakingQuizReader>();

if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services
        .AddOpenTelemetry()
        .WithTracing(tracing => tracing
            .AddSource(SpeakingTelemetry.ActivitySourceName)
            .AddSource(GenerativeAiTelemetry.ActivitySourceName)
            .AddSource(RealtimeTranslationTelemetry.ActivitySourceName))
        .WithMetrics(metrics => metrics
            .AddMeter(SpeakingTelemetry.MeterName)
            .AddMeter(GenerativeAiTelemetry.MeterName)
            .AddMeter(RealtimeTranslationTelemetry.MeterName))
        .UseAzureMonitor();
}

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

if (builder.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup"))
{
    await using var migrationScope = app.Services.CreateAsyncScope();
    var migrationLogger = migrationScope.ServiceProvider
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("DatabaseMigration");
    migrationLogger.LogInformation("Applying pending Glosify database migrations.");
    var migrationContext = migrationScope.ServiceProvider.GetRequiredService<GlosifyContext>();
    await migrationContext.Database.MigrateAsync();
    migrationLogger.LogInformation("Glosify database migrations are current.");
}

// Creates the shared demo account and tops it back up to its credit target. A no-op
// unless Demo:Enabled is set, and it runs after migrations because it writes to
// AspNetUsers and AiCreditAccounts.
{
    await using var demoScope = app.Services.CreateAsyncScope();
    await demoScope.ServiceProvider.GetRequiredService<DemoAccountSeeder>().EnsureAsync();
}

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
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

// Configure the HTTP request pipeline. In Development, WebApplication has already added
// the developer exception page; registering the handler unconditionally would sit inside
// it and swallow the exception before that page ever saw it.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

// Compression has to sit ahead of everything that writes a body, static assets
// included, so it sees the response before it is sent.
app.UseResponseCompression();

// MapStaticAssets publishes two routes per file: a fingerprinted one
// (css/site.3ty2x2i68v.css) marked immutable, and the plain path, marked
// no-cache because a deploy can change what it returns. The views link the
// plain path with asp-append-version, so every asset was revalidated on every
// page view — a conditional request per asset, per navigation.
//
// A request is safe to cache forever when its `v` is still the version the app
// would generate for that path right now: the URL then names content that
// cannot change under it. IFileVersionProvider is what asp-append-version used
// to build the URL in the first place, so asking it again compares like with
// like — and a `v` left over from an earlier deploy simply will not match, so
// it keeps the no-cache the endpoint chose.
//
// The ETag is deliberately not used for this. MapStaticAssets gives the
// brotli and gzip representations their own ETags, computed over the
// compressed bytes, so an ETag comparison would quietly fail for every
// browser — which all send Accept-Encoding — and only succeed for clients
// asking for identity.
app.Use(async (context, next) =>
{
    if (HttpMethods.IsGet(context.Request.Method)
        && context.Request.Query.TryGetValue("v", out var requestedFingerprint)
        && !string.IsNullOrEmpty(requestedFingerprint))
    {
        var versionProvider = context.RequestServices.GetRequiredService<IFileVersionProvider>();
        var versionedPath = versionProvider.AddFileVersionToPath(context.Request.PathBase, context.Request.Path);

        if (CurrentFileVersion(versionedPath) is { } current
            && string.Equals(current, requestedFingerprint, StringComparison.Ordinal))
        {
            context.Response.OnStarting(static state =>
            {
                var httpContext = (HttpContext)state;
                if (httpContext.Response.StatusCode == StatusCodes.Status200OK)
                {
                    httpContext.Response.Headers.CacheControl = "max-age=31536000, immutable";
                }

                return Task.CompletedTask;
            }, context);
        }
    }

    await next();
});

app.UseHttpsRedirection();

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(20),
});

// Security headers on every response. App scripts live in wwwroot/js and
// behaviors use data-* attributes instead of inline on* handlers; the only
// external script origin is jsDelivr for the pinned Three.js module.
// 'unsafe-inline' for styles remains because views use style attributes, while
// fonts.googleapis.com/gstatic.com serve the web fonts linked by the layout.
var configuredFormActionOrigins = builder.Configuration
    .GetSection("Security:Csp:FormActionOrigins")
    .Get<string[]>() ?? [];
// Extra connect-src entries for the ACS calling SDK (video signaling/media
// endpoints); configurable so new ACS domains don't require a code change.
var configuredConnectSources = builder.Configuration
    .GetSection("Security:Csp:ConnectSources")
    .Get<string[]>() ?? [];
configuredConnectSources =
[
    .. configuredConnectSources,
    .. BuildSpeechConnectSources(
        builder.Configuration["Speech:Region"],
        builder.Configuration["Speech:Endpoint"]),
];
var contentSecurityPolicy = BuildContentSecurityPolicy(configuredFormActionOrigins, configuredConnectSources);
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

app.MapHealthChecks("/healthz").AllowAnonymous();

app.MapStaticAssets().AllowAnonymous();

app.MapControllerRoute(
    name: "landing",
    pattern: "",
    defaults: new { controller = "Home", action = "Index" })
    .WithStaticAssets();

app.MapControllerRoute(
    name: "login",
    pattern: "login",
    defaults: new { controller = "Account", action = "Login" });

app.MapControllerRoute(
    name: "demo",
    pattern: "demo",
    defaults: new { controller = "Demo", action = "Index" });

app.MapControllerRoute(
    name: "quizzes",
    pattern: "Quizzes/{action=Index}/{id?}",
    defaults: new { controller = "Quiz" });

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

// Deliberately not AllowAnonymous: Identity UI protects /Identity/Account/Manage and
// /Account/Logout through AuthorizeAreaFolder/AuthorizeAreaPage conventions rather than
// attributes, and IAllowAnonymous metadata short-circuits the authorization middleware,
// which would silently disable those conventions. The pages that must stay open (Login,
// Register, ExternalLogin, the 2fa pages) carry their own [AllowAnonymous].
app.MapRazorPages();

app.MapHub<Glosify.Hubs.ClassroomChatHub>("/hubs/classroom-chat");

// Token auth endpoints for the mobile app (/api/auth/login, /register, /refresh, ...).
// AllowAnonymous is required because of the fallback authorization policy; the /manage
// endpoints in the group resolve the user from the bearer token and 404 without one.
app.MapGroup("/api/auth").MapIdentityApi<ApplicationUser>().AllowAnonymous();

// Microsoft Foundry agents call assistant tools back through here. The route carries a
// signed, short-lived session that names the acting user; the endpoint filter rejects a
// missing signing key, a bad shared secret, or an expired session.
app.MapAssistantMcp();


app.Run();
return 0;

/// <summary>
/// The version <see cref="IFileVersionProvider"/> just appended, or null when
/// it appended nothing because the file is not under the web root.
/// </summary>
static string? CurrentFileVersion(string versionedPath)
{
    const string marker = "?v=";
    var index = versionedPath.IndexOf(marker, StringComparison.Ordinal);
    return index < 0 ? null : versionedPath[(index + marker.Length)..];
}

static string BuildColdStartFriendlyConnectionString(string connectionString)
{
    var builder = new SqlConnectionStringBuilder(connectionString);
    if (builder.ConnectTimeout < 120)
    {
        builder.ConnectTimeout = 120;
    }

    return builder.ConnectionString;
}

static string BuildContentSecurityPolicy(IEnumerable<string> formActionOrigins, IEnumerable<string> extraConnectSources)
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

static IEnumerable<string> BuildSpeechConnectSources(string? region, string? endpoint)
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

public partial class Program { }
