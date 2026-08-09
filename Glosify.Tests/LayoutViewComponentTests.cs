using System.Security.Claims;
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
/// Covers the chrome the layout renders through view components. The panel's pickers used
/// to be loaded by the partial itself, so a failing lookup took the whole page with it.
/// <see cref="Chrome_still_renders_when_every_lookup_fails"/> pins the error path that
/// replaced that: narrowing either component's catch so the failure escapes turns the
/// request back into a 500, which is how it was verified to be load-bearing.
/// </summary>
public sealed class LayoutViewComponentTests
{
    [Fact]
    public async Task Assistant_panel_renders_the_context_pickers_it_loaded()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var document = await GetHomeAsync(client);

        Assert.NotNull(document.QuerySelector("[data-assistant-panel]"));
        var quizOptions = document
            .QuerySelectorAll("[data-assistant-quiz-selector] option")
            .Select(option => option.TextContent.Trim())
            .ToArray();
        Assert.Equal(
            ["No quiz selected", "Market Polish (English → Polish)"],
            quizOptions);

        var groups = document
            .QuerySelectorAll("[data-assistant-material-selector] optgroup")
            .Select(group => group.GetAttribute("label") ?? string.Empty)
            .ToArray();
        Assert.Equal(["Books", "Transcripts"], groups);
        Assert.Equal(
            "Tatry Guide",
            document.QuerySelector("[data-assistant-material-selector] optgroup[label='Books'] option")
                ?.TextContent.Trim());
        Assert.Equal(
            "Kraków market",
            document.QuerySelector("[data-assistant-material-selector] optgroup[label='Transcripts'] option")
                ?.TextContent.Trim());
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
    /// Every lookup behind the chrome throws. The page must still be the page the user
    /// asked for: a missing dropdown or credit badge is not a reason to serve a 500.
    /// </summary>
    [Fact]
    public async Task Chrome_still_renders_when_every_lookup_fails()
    {
        using var factory = CreateFactory(failing: true);
        var client = factory.CreateClient();

        var document = await GetHomeAsync(client);

        Assert.NotNull(document.QuerySelector("[data-assistant-panel]"));
        Assert.Null(document.QuerySelector(".credit-pill"));
        Assert.Equal(
            ["No quiz selected"],
            document.QuerySelectorAll("[data-assistant-quiz-selector] option")
                .Select(option => option.TextContent.Trim()));
        Assert.Empty(document.QuerySelectorAll("[data-assistant-material-selector] optgroup"));
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
                if (failing)
                {
                    services.AddSingleton<IAiCreditService, ThrowingChromeServices>();
                    services.AddSingleton<IQuizService, ThrowingChromeServices>();
                    services.AddSingleton<IBookDocumentService, ThrowingChromeServices>();
                    services.AddSingleton<IRealtimeTranslationTranscriptService, ThrowingChromeServices>();
                    services.AddSingleton<IQuizLanguagePreferenceService, ThrowingChromeServices>();
                }
                else
                {
                    services.AddSingleton<IAiCreditService, StubChromeServices>();
                    services.AddSingleton<IQuizService, StubChromeServices>();
                    services.AddSingleton<IBookDocumentService, StubChromeServices>();
                    services.AddSingleton<IRealtimeTranslationTranscriptService, StubChromeServices>();
                    services.AddSingleton<IQuizLanguagePreferenceService, StubChromeServices>();
                }

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
        public bool HasLanguage => CurrentLanguage is not null;
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
