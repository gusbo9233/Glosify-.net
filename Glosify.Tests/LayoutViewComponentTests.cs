using System.Security.Claims;
using System.Text.Json;
using System.Text.Encodings.Web;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Glosify.Models.Entities;
using Glosify.Models.Library;
using Glosify.Services.Ai;
using Glosify.Services.Books;
using Glosify.Services.Language;
using Glosify.Services.Quizzes;
using Glosify.Services.RealtimeTranslation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

/// <summary>
/// Covers the chrome the layout renders through view components. Assistant libraries are
/// fetched only after the panel opens, and a failure in one optional picker must not turn
/// either the page or the options request into a 500.
/// </summary>
public sealed class LayoutViewComponentTests
{
    [Fact]
    public async Task Assistant_panel_defers_context_picker_lookups_until_requested()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var document = await GetHomeAsync(client);
        var services = factory.Services.GetRequiredService<ChromeServicesBase>();

        var panel = Assert.IsAssignableFrom<IElement>(document.QuerySelector("[data-assistant-panel]"));
        Assert.Equal("/Assistant/ContextOptions", panel.GetAttribute("data-context-options-url"));
        Assert.Equal(0, services.QuizLibraryCalls);
        Assert.Equal(0, services.BookLibraryCalls);
        Assert.Equal(0, services.LanguagePreferenceCalls);
        Assert.Equal(0, services.TranscriptLibraryCalls);

        var response = await client.GetAsync("/Assistant/ContextOptions");
        response.EnsureSuccessStatusCode();
        using var options = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("Market Polish", options.RootElement.GetProperty("quizzes")[0].GetProperty("name").GetString());
        Assert.Equal("Tatry Guide", options.RootElement.GetProperty("books")[0].GetProperty("title").GetString());
        Assert.Equal("Kraków market", options.RootElement.GetProperty("transcripts")[0].GetProperty("title").GetString());
        Assert.Equal(1, services.QuizLibraryCalls);
        Assert.Equal(1, services.BookLibraryCalls);
        Assert.Equal(1, services.LanguagePreferenceCalls);
        Assert.Equal(1, services.TranscriptLibraryCalls);
    }

    [Fact]
    public async Task Credit_pill_shows_the_balance_and_the_language_pill_the_selection()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var document = await GetHomeAsync(client);

        Assert.Contains("42 credits", document.QuerySelector(".credit-pill")?.TextContent ?? string.Empty);
        // learner@example.test is not in Admin:Emails, so the balance is not a link.
        Assert.Equal("span", document.QuerySelector(".credit-pill")?.LocalName);
        Assert.EndsWith(
            "Polish",
            document.QuerySelector(".language-context-pill-active")?.TextContent.Trim(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Every lookup behind the chrome throws. The page and the best-effort options endpoint
    /// must still respond; a missing dropdown or credit badge is not a reason to serve a 500.
    /// </summary>
    [Fact]
    public async Task Chrome_still_renders_when_every_lookup_fails()
    {
        using var factory = CreateFactory(failing: true);
        var client = factory.CreateClient();

        var document = await GetHomeAsync(client);

        Assert.NotNull(document.QuerySelector("[data-assistant-panel]"));
        Assert.Null(document.QuerySelector(".credit-pill"));
        Assert.Equal(["No quiz selected"], document
            .QuerySelectorAll("[data-assistant-quiz-selector] option")
            .Select(option => option.TextContent.Trim()));
        Assert.Empty(document.QuerySelectorAll("[data-assistant-material-selector] optgroup"));

        var response = await client.GetAsync("/Assistant/ContextOptions");
        response.EnsureSuccessStatusCode();
        using var options = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, options.RootElement.GetProperty("quizzes").GetArrayLength());
        Assert.Equal(0, options.RootElement.GetProperty("books").GetArrayLength());
        Assert.Equal(0, options.RootElement.GetProperty("transcripts").GetArrayLength());
    }

    private static async Task<IDocument> GetHomeAsync(HttpClient client)
    {
        var response = await client.GetAsync("/Home/Index");
        response.EnsureSuccessStatusCode();
        return await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync());
    }

    private static WebApplicationFactory<Program> CreateFactory(bool failing = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAiCreditService>();
                services.RemoveAll<IQuizService>();
                services.RemoveAll<IBookDocumentService>();
                services.RemoveAll<IRealtimeTranslationTranscriptService>();
                services.RemoveAll<IQuizLanguagePreferenceService>();
                services.RemoveAll<ILanguageContext>();

                services.AddSingleton<ILanguageContext>(new FixedLanguageContext("Polish"));
                ChromeServicesBase chrome = failing
                    ? new ThrowingChromeServices()
                    : new StubChromeServices();
                services.AddSingleton(chrome);
                services.AddSingleton<IAiCreditService>(chrome);
                services.AddSingleton<IQuizService>(chrome);
                services.AddSingleton<IBookDocumentService>(chrome);
                services.AddSingleton<IRealtimeTranslationTranscriptService>(chrome);
                services.AddSingleton<IQuizLanguagePreferenceService>(chrome);

                services
                    .AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthHandler.TestScheme;
                        options.DefaultChallengeScheme = TestAuthHandler.TestScheme;
                        options.DefaultForbidScheme = TestAuthHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.TestScheme,
                        _ => { });
            });
        });

    private sealed class FixedLanguageContext(string? currentLanguage) : ILanguageContext
    {
        public string? CurrentLanguage { get; } = currentLanguage;
        public IReadOnlyList<string> SupportedLanguages { get; } = ["Polish"];
        public bool TrySetLanguage(string language) => false;
        public void Clear()
        {
        }
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "LayoutTests";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            Claim[] claims =
            [
                new(ClaimTypes.NameIdentifier, "learner-1"),
                new(ClaimTypes.Email, "learner@example.test"),
                new(ClaimTypes.Name, "learner@example.test"),
            ];
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, TestScheme));
            return Task.FromResult(
                AuthenticateResult.Success(new AuthenticationTicket(principal, TestScheme)));
        }
    }
}
