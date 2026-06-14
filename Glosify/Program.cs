using Glosify.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Glosify.Models;
using Glosify.Services;
using Glosify.Services.Quizzes;
using Glosify.Services.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

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
});
builder.Services.AddMemoryCache();

// Add Identity
builder.Services.AddDefaultIdentity<ApplicationUser>(options =>
{
    options.SignIn.RequireConfirmedAccount = false;
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
builder.Services.AddScoped<IFlashcardSessionService, FlashcardSessionService>();
builder.Services.AddScoped<ITypingQuizService, TypingQuizService>();
builder.Services.AddScoped<ITypingSessionService, TypingSessionService>();
builder.Services.AddSingleton<IBookFileStorage, AzureBlobBookFileStorage>();
builder.Services.AddScoped<IPdfTextExtractionService, PdfPigTextExtractionService>();
builder.Services.AddScoped<IBookDocumentService, BookDocumentService>();
builder.Services.AddSingleton<IGeminiClient, GeminiClient>();
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

// Configure the HTTP request pipeline.
app.UseExceptionHandler("/Home/Error");
if (!app.Environment.IsDevelopment())
{
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthentication();
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

public partial class Program { }
