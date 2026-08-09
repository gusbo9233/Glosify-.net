using Azure.Monitor.OpenTelemetry.AspNetCore;
using Glosify.Data;
using Glosify.Extensions;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Assistant.Mcp;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Auth;
using Glosify.Services.RealtimeTranslation;
using Glosify.Services.Speaking;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
});

// Gives the bearer-token API surface a consistent RFC 7807 error body instead of an
// empty response, and backs UseStatusCodePages below.
builder.Services.AddProblemDetails();

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

builder.Services.AddGlosifyRateLimiting();

builder.Services.AddGlosifyAuthentication(builder.Configuration, builder.Environment);


builder.Services.AddRazorPages();

builder.Services.AddGlosifyServices(builder.Configuration, builder.Environment);

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

// A bare 404 or 403 otherwise goes back with no body at all. With AddProblemDetails
// above, API callers get RFC 7807 JSON and browsers get a short plain-text line.
app.UseStatusCodePages();

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
app.UseGlosifySecurityHeaders(builder.Configuration);

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


public partial class Program { }
