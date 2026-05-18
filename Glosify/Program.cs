using Glosify.Data;
using Glosify.Data.Importing;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Glosify.Models;
using Glosify.Models.LanguageConfig;
using Glosify.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
});
builder.Services.AddMemoryCache();

// Add Identity
builder.Services.AddDefaultIdentity<ApplicationUser>(options =>
{
    options.SignIn.RequireConfirmedAccount = false;
})
    .AddEntityFrameworkStores<GlosifyContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/login";
});

var authenticationBuilder = builder.Services.AddAuthentication();
var googleClientId = builder.Configuration["Authentication:Google:ClientId"];
var googleClientSecret = builder.Configuration["Authentication:Google:ClientSecret"];
if (!string.IsNullOrWhiteSpace(googleClientId) && !string.IsNullOrWhiteSpace(googleClientSecret))
{
    authenticationBuilder.AddGoogle(options =>
    {
        options.ClientId = googleClientId;
        options.ClientSecret = googleClientSecret;
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

    var thinkingLevel = builder.Configuration["GEMINI_THINKING_LEVEL"];
    if (!string.IsNullOrWhiteSpace(thinkingLevel))
    {
        options.ThinkingLevel = thinkingLevel;
    }

    var timeoutSeconds = builder.Configuration["GEMINI_TIMEOUT_SECONDS"];
    if (int.TryParse(timeoutSeconds, out var parsedTimeoutSeconds) && parsedTimeoutSeconds > 0)
    {
        options.TimeoutSeconds = parsedTimeoutSeconds;
    }
});

// Register application services
builder.Services.AddScoped<IQuizService, QuizService>();
builder.Services.AddScoped<IWordService, WordService>();
builder.Services.AddScoped<IFlashcardSessionService, FlashcardSessionService>();
builder.Services.AddScoped<ITypingQuizService, TypingQuizService>();
builder.Services.AddSingleton<IGeminiClient, GeminiClient>();
builder.Services.AddScoped<IVocabularyGenerationService, LlmVocabularyGenerationService>();
builder.Services.AddScoped<IImageTextExtractionService, LlmImageTextExtractionService>();
builder.Services.AddScoped<IGeneratedVocabularyService, GeneratedVocabularyService>();
builder.Services.AddScoped<IWordDetailEnrichmentService, WordDetailEnrichmentService>();
builder.Services.AddScoped<IWordDetailViewModelService, WordDetailViewModelService>();
builder.Services.AddScoped<IAssistantTools, AssistantTools>();
builder.Services.AddScoped<IChangeApplier, ChangeApplier>();
builder.Services.AddScoped<IAssistantOrchestrator, AssistantOrchestrator>();

builder.Services.AddSingleton<ILanguageDictionaryConfig, GermanDictionaryConfig>();
builder.Services.AddSingleton<ILanguageDictionaryConfig, EstonianDictionaryConfig>();
builder.Services.AddSingleton<ILanguageDictionaryConfig, UkrainianDictionaryConfig>();
builder.Services.AddSingleton<ILanguageDictionaryConfig, PolishDictionaryConfig>();

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

if (KaikkiImportCommand.IsRequested(args))
{
    return await KaikkiImportCommand.RunAsync(app.Services, args);
}

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

app.MapStaticAssets();

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

app.MapRazorPages();


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
