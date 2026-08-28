using Azure.Monitor.OpenTelemetry.AspNetCore;
using Glosify.Data;
using Glosify.Extensions;
using Glosify.Infrastructure.Api;
using Glosify.Infrastructure.Health;
using Glosify.Localization;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Auth;
using Glosify.Services.RealtimeTranslation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

const string browserTestingEnvironment = "BrowserTesting";
const string browserTestTokenHeader = "X-Glosify-Browser-Test-Token";
var browserTestRunToken = builder.Environment.IsEnvironment(browserTestingEnvironment)
    ? builder.Configuration["BrowserTests:RunToken"]
    : null;
if (builder.Environment.IsEnvironment(browserTestingEnvironment)
    && string.IsNullOrWhiteSpace(browserTestRunToken))
{
    throw new InvalidOperationException(
        "BrowserTests:RunToken is required when ASPNETCORE_ENVIRONMENT is BrowserTesting.");
}

// Add services to the container.
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.AddSingleton<Microsoft.Extensions.Localization.IStringLocalizerFactory, UiTextStringLocalizerFactory>();
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
    options.Filters.Add<ApiProblemDetailsResultFilter>();
    options.Filters.AddService<ApiExceptionFilter>();
})
    .AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix)
    .AddDataAnnotationsLocalization(options =>
    {
        options.DataAnnotationLocalizerProvider = (_, factory) =>
            factory.Create(typeof(UiText));
    });
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture(DisplayCultureCatalog.DefaultCulture);
    options.SupportedCultures = DisplayCultureCatalog.CultureInfos;
    options.SupportedUICultures = DisplayCultureCatalog.CultureInfos;
    options.ApplyCurrentCultureToResponseHeaders = true;
    options.RequestCultureProviders =
    [
        new LocalizedPublicRequestCultureProvider(),
        new DisplayCultureClaimRequestCultureProvider(),
        new CookieRequestCultureProvider(),
    ];
});
builder.Services.Configure<RouteOptions>(options =>
{
    options.ConstraintMap["displayCulture"] = typeof(DisplayCultureRouteConstraint);
    options.ConstraintMap["unsupportedDisplayCulture"] = typeof(UnsupportedDisplayCultureRouteConstraint);
});
builder.Services.AddScoped<ApiExceptionFilter>();
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = GlosifyProblemDetails.ValidationResult;
});

// Gives the bearer-token API surface a consistent RFC 7807 error body instead of an
// empty response, and backs UseStatusCodePages below.
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        var statusCode = context.ProblemDetails.Status ?? context.HttpContext.Response.StatusCode;
        GlosifyProblemDetails.AddCommonExtensions(
            context.HttpContext,
            context.ProblemDetails,
            GlosifyProblemDetails.CodeForStatus(statusCode));
    };
});
builder.Services.AddOpenApi();

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

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    var adminEmails = builder.Configuration.GetSection("Admin:Emails").Get<string[]>() ?? [];
    bool IsAdmin(AuthorizationHandlerContext context)
    {
        var email = context.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
            ?? context.User.Identity?.Name
            ?? string.Empty;
        return adminEmails.Any(adminEmail => string.Equals(
            adminEmail,
            email,
            StringComparison.OrdinalIgnoreCase));
    }

    options.AddPolicy(AuthorizationPolicyNames.AiCreditAdmin, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(IsAdmin);
    });
});
builder.Services.AddMemoryCache();

// Liveness only, deliberately: a transient SQL outage must not cause App Service
// Health Check to recycle an otherwise sound web worker.
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseReadinessHealthCheck>("database", tags: ["ready"]);

builder.Services.AddGlosifyRateLimiting();

builder.Services.AddGlosifyAuthentication(builder.Configuration, builder.Environment);


builder.Services.AddRazorPages()
    .AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix)
    .AddDataAnnotationsLocalization(options =>
    {
        options.DataAnnotationLocalizerProvider = (_, factory) =>
            factory.Create(typeof(UiText));
    });

builder.Services.AddGlosifyServices(builder.Configuration, builder.Environment);

if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services
        .AddOpenTelemetry()
        .WithTracing(tracing => tracing
            .AddSource(GenerativeAiTelemetry.ActivitySourceName)
            .AddSource(RealtimeTranslationTelemetry.ActivitySourceName))
        .WithMetrics(metrics => metrics
            .AddMeter(GenerativeAiTelemetry.MeterName)
            .AddMeter(RealtimeTranslationTelemetry.MeterName)
            .AddMeter(DisplayLanguageTelemetry.MeterName))
        .UseAzureMonitor(options =>
        {
            options.SamplingRatio = 1.0F;
            options.TracesPerSecond = null;
        });
}

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Connection string 'DefaultConnection' is not configured. Set ConnectionStrings__DefaultConnection in the host environment or appsettings.Development.json for local development.");
}
var sqlConnectionString = BuildResilientSqlConnectionString(connectionString);

// Configure SQL Server database. The factory gives budget-closure writes their own
// change tracker, so returning a 503 cannot flush unrelated request state.
void ConfigureGlosifyContext(DbContextOptionsBuilder options) =>
    options.UseSqlServer(
        sqlConnectionString,
        sqlOptions =>
        {
            // Keep enough headroom for transient Azure SQL/network delays and retry
            // throttling without changing the configured Basic 5-DTU database tier.
            sqlOptions.CommandTimeout(120);
            sqlOptions.EnableRetryOnFailure(
                maxRetryCount: 10,
                maxRetryDelay: TimeSpan.FromSeconds(30),
                errorNumbersToAdd: null);
        });
builder.Services.AddDbContext<GlosifyContext>(ConfigureGlosifyContext);
builder.Services.AddDbContextFactory<GlosifyContext>(
    ConfigureGlosifyContext,
    ServiceLifetime.Scoped);

var app = builder.Build();

// Creates the shared demo account and tops it back up to its credit target. A no-op
// unless Demo:Enabled is set. Database migrations are applied by deployment tooling
// before the application starts, so runtime identities never need schema permissions.
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

// A bare 404 or 403 otherwise goes back with no body at all. With AddProblemDetails
// above, API callers get RFC 7807 JSON and browsers get a short plain-text line.
app.UseStatusCodePages();

// Static assets already have build-time compressed representations. Wrapping their
// send-file responses in dynamic compression can produce a Content-Encoding header
// with an empty body on some hosts, so dynamic compression is reserved for routed
// HTML/JSON responses (whose URLs do not have file extensions).
app.UseWhen(
    context => !Path.HasExtension(context.Request.Path),
    branch => branch.UseResponseCompression());

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
app.UseGlosifySecurityHeaders(builder.Configuration);

app.UseRouting();

// Authentication must run before the rate limiter so the assistant limit can be
// partitioned per user rather than per IP.
app.UseAuthentication();

// Account culture is stored in the authenticated principal. Keep localization after
// authentication, but before every component that can produce a routed response.
app.UseRequestLocalization();

app.UseRateLimiter();

app.UseAuthorization();

// The static-asset endpoint intentionally answers unsupported HTTP methods with 405.
// Short-circuit the retired custom-quiz paths after authorization so every former
// authenticated endpoint instead has the same 404 contract, without keeping MVC routes.
app.Use(async (context, next) =>
{
    if (IsRetiredCustomQuizPath(context.Request.Path))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await next();
});

if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
    app.MapOpenApi().AllowAnonymous();
}

app.MapHealthChecks("/healthz", new HealthCheckOptions
{
    Predicate = _ => false,
}).AllowAnonymous();
app.MapHealthChecks("/readyz", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
}).AllowAnonymous();

// A browser suite must prove it is talking to the explicitly launched test host before
// it creates accounts or mutates data. Keep the endpoint absent in every other environment
// and require a per-run token so an accidentally configured URL fails closed.
if (app.Environment.IsEnvironment(browserTestingEnvironment))
{
    app.MapGet("/_test/browser-handshake", (HttpContext context) =>
    {
        var suppliedTokens = context.Request.Headers[browserTestTokenHeader];
        if (suppliedTokens.Count != 1
            || !string.Equals(
                suppliedTokens[0],
                browserTestRunToken,
                StringComparison.Ordinal))
        {
            return Results.NotFound();
        }

        return Results.NoContent();
    }).AllowAnonymous();
}

var deploymentCommitPath = Path.Combine(app.Environment.ContentRootPath, "deployment-commit.txt");
var deploymentCommit = File.Exists(deploymentCommitPath)
    ? File.ReadAllText(deploymentCommitPath).Trim()
    : "local";
app.MapGet("/deployment-version", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Text(deploymentCommit);
}).AllowAnonymous();

app.MapStaticAssets().AllowAnonymous();

app.MapControllerRoute(
    name: "localized-privacy",
    pattern: "{culture:displayCulture}/privacy",
    defaults: new { controller = "Home", action = "LocalizedPrivacy" });

app.MapControllerRoute(
    name: "localized-terms",
    pattern: "{culture:displayCulture}/terms",
    defaults: new { controller = "Home", action = "LocalizedTerms" });

app.MapControllerRoute(
    name: "localized-support",
    pattern: "{culture:displayCulture}/support",
    defaults: new { controller = "Home", action = "LocalizedSupport" });

app.MapControllerRoute(
    name: "localized-landing",
    pattern: "{culture:displayCulture}",
    defaults: new { controller = "Home", action = "LocalizedIndex" })
    .WithStaticAssets();

app.MapGet("/{culture:unsupportedDisplayCulture}/{page:regex(privacy|terms|support)}", () => Results.NotFound())
    .AllowAnonymous();
app.MapGet("/{culture:unsupportedDisplayCulture}", () => Results.NotFound())
    .AllowAnonymous();

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

// Token auth endpoints for the mobile app (/api/auth/login, /register, /refresh, ...).
// AllowAnonymous is required because of the fallback authorization policy; the /manage
// endpoints in the group resolve the user from the bearer token and 404 without one.
app.MapGroup("/api/auth").MapIdentityApi<ApplicationUser>().AllowAnonymous();

app.Run();
return 0;

static bool IsRetiredCustomQuizPath(PathString path)
{
    if (path.StartsWithSegments("/CustomQuizzes"))
    {
        return true;
    }

    var segments = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
    return segments is { Length: 4 }
        && string.Equals(segments[0], "Quizzes", StringComparison.OrdinalIgnoreCase)
        && Guid.TryParse(segments[1], out _)
        && string.Equals(segments[2], "Custom", StringComparison.OrdinalIgnoreCase)
        && string.Equals(segments[3], "New", StringComparison.OrdinalIgnoreCase);
}

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

static string BuildResilientSqlConnectionString(string connectionString)
{
    var builder = new SqlConnectionStringBuilder(connectionString);
    if (builder.ConnectTimeout < 120)
    {
        builder.ConnectTimeout = 120;
    }

    return builder.ConnectionString;
}


public partial class Program { }
