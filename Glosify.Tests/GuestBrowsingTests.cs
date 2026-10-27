using System.Net;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Glosify.Tests;

public sealed class GuestBrowsingTests : IClassFixture<GuestWebApplicationFactory>
{
    private readonly HttpClient _client;
    public GuestBrowsingTests(GuestWebApplicationFactory factory) =>
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Theory]
    [InlineData("/Books", "books")]
    [InlineData("/Quiz", "quiz")]
    [InlineData("/Anki", "anki")]
    [InlineData("/Transcripts", "transcripts")]
    [InlineData("/Translations", "translations")]
    public async Task GuestLibraries_RenderBundledSamplesWithoutAnAccount(string route, string section)
    {
        var response = await _client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = new HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync());
        var selector = section switch { "books" => ".book-card", "quiz" => ".quiz-card", "anki" => "[data-anki-study]", "transcripts" => ".transcript-card", _ => ".saved-translation-card" };
        Assert.NotNull(document.QuerySelector(selector));
        if (section != "anki") Assert.NotNull(document.QuerySelector($"a[href='/preview/{section}/sample']"));
        if (section == "quiz") Assert.Null(document.QuerySelector(".quiz-card .status-pill"));
        Assert.Null(document.QuerySelector(".guest-preview"));
        Assert.NotNull(document.QuerySelector("[data-login-required]"));
        Assert.NotNull(document.QuerySelector("[data-login-dialog]"));
        Assert.DoesNotContain(response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [], value => value.Contains(".AspNetCore.Identity.Application="));
    }

    [Theory]
    [InlineData("/preview/books/sample")]
    [InlineData("/preview/quiz/sample")]
    [InlineData("/preview/anki/sample")]
    [InlineData("/preview/transcripts/sample")]
    [InlineData("/preview/translations/sample")]
    [InlineData("/preview/quiz/sample/practice/typing")]
    [InlineData("/preview/anki/sample/practice/flashcards")]
    public async Task Samples_ArePublic(string route) =>
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(route)).StatusCode);

    [Fact]
    public async Task TranscriptSample_UsesRealReaderAndStreamTabs()
    {
        var source = new HtmlParser().ParseDocument(await _client.GetStringAsync("/preview/transcripts/sample"));
        Assert.Equal(6, source.QuerySelectorAll(".transcript-segment").Length);
        Assert.Contains("buenos días", source.QuerySelector(".transcript-reader")!.TextContent);
        var translationLink = source.QuerySelector(".transcript-streams a[href*='translation']")!;
        var translation = new HtmlParser().ParseDocument(await _client.GetStringAsync(translationLink.GetAttribute("href")));
        Assert.Contains("good morning", translation.QuerySelector(".transcript-reader")!.TextContent);
        Assert.All(source.QuerySelectorAll(".transcript-actions form"), form => Assert.Equal("/login", form.GetAttribute("action")));
    }

    [Theory]
    [InlineData("typing", ".typing-prompt")]
    [InlineData("flashcards", ".card-word")]
    public async Task ReversePractice_PlacesSpanishSpeechOnThePrompt(string mode, string selector)
    {
        var page = new HtmlParser().ParseDocument(await _client.GetStringAsync($"/preview/quiz/sample/practice/{mode}?PracticeDirection=target-to-source"));
        var speech = page.QuerySelector(selector + " [data-tts]")!;
        Assert.Equal("hola", speech.GetAttribute("data-tts"));
        Assert.Equal("Spanish", speech.GetAttribute("data-tts-lang"));
        Assert.Null(page.QuerySelector(".card-answer [data-tts]"));
    }

    [Fact]
    public async Task SampleLanguagePicker_KeepsTheSampleDestination()
    {
        var page = new HtmlParser().ParseDocument(await _client.GetStringAsync("/preview/transcripts/sample?stream=translation"));
        var link = page.QuerySelector(".language-context-pill")!.GetAttribute("href")!;
        Assert.Contains("returnUrl=%2Fpreview%2Ftranscripts%2Fsample%3Fstream%3Dtranslation", link);
    }

    [Fact]
    public async Task SampleBook_UsesRealPdfReaderAndBundledPdf()
    {
        var page = new HtmlParser().ParseDocument(await _client.GetStringAsync("/preview/books/sample"));
        Assert.NotNull(page.QuerySelector("[data-pdf-canvas]"));
        Assert.Equal("Spanish", page.QuerySelector("[data-reading-language]")!.GetAttribute("data-reading-language"));
        var pdfUrl = page.QuerySelector("[data-pdf-url]")!.GetAttribute("data-pdf-url");
        var pdf = await _client.GetAsync(pdfUrl);
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(await pdf.Content.ReadAsByteArrayAsync()));
    }

    [Theory]
    [InlineData("/preview/quiz/sample", ".quiz-workspace")]
    [InlineData("/preview/quiz/sample/settings", ".settings-shell")]
    [InlineData("/preview/quiz/sample/practice/typing", "[data-typing-quiz]")]
    [InlineData("/preview/quiz/sample/practice/flashcards", "[data-flashcard-quiz]")]
    [InlineData("/preview/anki/sample", "[data-anki-study]")]
    [InlineData("/preview/translations/sample", ".saved-translation-session-list")]
    public async Task Samples_RenderTheProductionFeatureViews(string path, string selector)
    {
        var html = await _client.GetStringAsync(path);
        var page = new HtmlParser().ParseDocument(html);
        Assert.NotNull(page.QuerySelector(selector));
        Assert.Null(page.QuerySelector(".guest-preview"));
        Assert.Contains("guest-real-ui.js", html);
        Assert.DoesNotContain("/js/flashcard-quiz.js", html);
        Assert.DoesNotContain("/js/typing-quiz.js", html);
    }

    [Fact]
    public async Task Quiz_controls_have_explicit_names_and_range_descriptions()
    {
        var library = new HtmlParser().ParseDocument(await _client.GetStringAsync("/Quiz"));
        var prompt = library.QuerySelector("[data-json-import-generation-prompt]");
        Assert.NotNull(prompt);
        var promptLabel = library.GetElementById(prompt.GetAttribute("aria-labelledby")!);
        Assert.NotNull(promptLabel);
        Assert.Contains("instructions", promptLabel.TextContent, StringComparison.OrdinalIgnoreCase);

        var settings = new HtmlParser().ParseDocument(await _client.GetStringAsync("/preview/quiz/sample/settings"));
        foreach (var (name, label) in new[] { ("WordRangeStart", "Range start (%)"), ("WordRangeEnd", "Range end (%)") })
        {
            var input = settings.QuerySelector($"input[name='{name}']");
            Assert.NotNull(input);
            Assert.Equal(label, input.GetAttribute("aria-label"));
            var description = settings.GetElementById(input.GetAttribute("aria-describedby")!);
            Assert.NotNull(description);
            Assert.Contains("oldest to newest", description.TextContent, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal("Filter words…", settings.QuerySelector("[data-word-picker-search]")?.GetAttribute("aria-label"));
    }

    [Fact]
    public async Task TypingResultsRestart_RemainsLocalPractice()
    {
        var page = new HtmlParser().ParseDocument(await _client.GetStringAsync("/preview/quiz/sample/practice/typing"));
        var restart = page.QuerySelector(".typing-result-actions form[data-sample-restart-form]");
        Assert.NotNull(restart);
        Assert.False(restart.HasAttribute("data-sample-account-form"));
    }

    [Fact]
    public async Task SampleDisplayLanguageSelector_PostsToRealEndpointAndReturnsToSample()
    {
        var page = new HtmlParser().ParseDocument(await _client.GetStringAsync("/preview/books/sample"));
        var form = page.QuerySelector("form.display-language-selector")!;
        Assert.Equal("/display-language", form.GetAttribute("action"));
        Assert.Equal("post", form.GetAttribute("method"));
        Assert.False(form.HasAttribute("data-sample-account-form"));
        var returnUrl = Assert.Single(form.QuerySelectorAll("input[name='returnUrl']")).GetAttribute("value")!;
        var response = await _client.PostAsync(form.GetAttribute("action"), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = form.QuerySelector("input[name='__RequestVerificationToken']")!.GetAttribute("value")!,
            ["culture"] = "sv-SE", ["returnUrl"] = returnUrl
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/preview/books/sample", response.Headers.Location!.OriginalString);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith(CookieRequestCultureProvider.DefaultCookieName + "=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SignedInSample_KeepsSignOutAndDisplayLanguageForms()
    {
        using var app = new AvatarFixture(); using var client = await app.Client("learner");
        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/preview/quiz/sample"));
        Assert.NotNull(page.QuerySelector("[data-guest-return]"));
        var logout = page.QuerySelector(".app-navigation-account form[action='/Account/Logout']")!;
        Assert.Equal("post", logout.GetAttribute("method"));
        Assert.False(logout.HasAttribute("data-sample-account-form"));
        Assert.Null(logout.QuerySelector("input[name='returnUrl']"));
        var language = page.QuerySelector("form.display-language-selector")!;
        Assert.Equal("/display-language", language.GetAttribute("action"));
        Assert.False(language.HasAttribute("data-sample-account-form"));
    }

    [Theory]
    [InlineData("/preview/books/not-a-sample")]
    [InlineData("/preview/private/sample")]
    [InlineData("/preview/books/sample/practice/typing")]
    [InlineData("/preview/quiz/sample/practice/unknown")]
    public async Task InvalidSamples_AreNotFound(string route) =>
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(route)).StatusCode);

    [Theory]
    [InlineData("/Books/Read/11111111-1111-1111-1111-111111111111")]
    [InlineData("/Quiz/Details/11111111-1111-1111-1111-111111111111")]
    [InlineData("/Transcripts/11111111-1111-1111-1111-111111111111")]
    [InlineData("/Translations/11111111-1111-1111-1111-111111111111")]
    [InlineData("/FlashcardQuiz")]
    [InlineData("/TypingQuiz")]
    public async Task PersonalResources_StillRequireLogin(string route)
    {
        var response = await _client.GetAsync(route);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/login", response.Headers.Location!.OriginalString, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/Books/Upload")]
    [InlineData("/Quizzes/Create")]
    [InlineData("/Anki/AddQuiz")]
    [InlineData("/Assistant/GlobalSend")]
    public async Task PersonalMutations_ChallengeWithoutExecuting(string route)
    {
        var response = await _client.PostAsync(route, new StringContent(""));
        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PaidSpeechApi_RemainsUnauthorized()
    {
        var response = await _client.GetAsync("/api/tts/voices?lang=es");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GuestLanguageSelection_UsesCookieAndRejectsExternalRedirect()
    {
        var page = await _client.GetStringAsync("/Languages");
        var token = new HtmlParser().ParseDocument(page).QuerySelector("input[name='__RequestVerificationToken']")!.GetAttribute("value")!;
        var response = await _client.PostAsync("/Languages/Select", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["language"] = "Spanish", ["returnUrl"] = "https://example.com", ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.DoesNotContain("example.com", response.Headers.Location!.OriginalString);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith("glosify.guest-language=Spanish", StringComparison.Ordinal));
        var library = await _client.GetStringAsync("/Quiz");
        Assert.Contains("/preview/quiz/sample", library);
    }

    public static IEnumerable<object[]> SupportedSamples =>
        Glosify.Services.Language.QuizLanguageCatalog.All.Select(language => new object[] { language.Code, language.Name });

    [Theory]
    [MemberData(nameof(SupportedSamples))]
    public async Task EveryLanguage_HasMatchingLibrariesPracticeAndBook(string code, string name)
    {
        _client.DefaultRequestHeaders.Add("Cookie", $"glosify.guest-language={Uri.EscapeDataString(name)}");
        foreach (var section in new[] { "Books", "Quiz", "Anki", "Transcripts", "Translations" })
        {
            var page = new HtmlParser().ParseDocument(await _client.GetStringAsync($"/{section}"));
            Assert.Equal(code, page.Body!.GetAttribute("data-guest-language-code"));
            Assert.Contains(name, page.QuerySelector("[data-guest-return]")!.TextContent);
            var cards = System.Text.Json.JsonSerializer.Deserialize<Glosify.Services.Guest.GuestCard[]>(page.Body.GetAttribute("data-guest-cards")!)!;
            Assert.Equal(5, cards.Length);
            Assert.All(cards, card => { Assert.False(string.IsNullOrWhiteSpace(card.Source)); Assert.False(string.IsNullOrWhiteSpace(card.Target)); });
            if (section != "Anki") Assert.NotNull(page.QuerySelector($"a[href='/preview/{section.ToLowerInvariant()}/sample']"));
        }
        var practice = new HtmlParser().ParseDocument(await _client.GetStringAsync("/preview/quiz/sample/practice/typing?PracticeDirection=target-to-source"));
        var payload = System.Text.Json.JsonSerializer.Deserialize<Glosify.Services.Guest.GuestCard[]>(practice.Body!.GetAttribute("data-guest-cards")!)!;
        Assert.Contains(payload[0].Target, practice.QuerySelector(".typing-prompt")!.TextContent);
        if (code == "free") Assert.Null(practice.QuerySelector(".typing-prompt [data-tts]"));
        else Assert.Equal(name, practice.QuerySelector(".typing-prompt [data-tts]")!.GetAttribute("data-tts-lang"));
        var reader = new HtmlParser().ParseDocument(await _client.GetStringAsync("/preview/books/sample"));
        Assert.Equal(code == "free" ? "English" : name, reader.QuerySelector("[data-reading-language]")!.GetAttribute("data-reading-language"));
        Assert.Equal($"/samples/starter-{code}.pdf", reader.QuerySelector("[data-pdf-url]")!.GetAttribute("data-pdf-url"));
        var pdf = await _client.GetAsync($"/samples/starter-{code}.pdf");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task PolishLegacyCookie_ControlsEverySampleAndGuestSelectionOverridesIt()
    {
        _client.DefaultRequestHeaders.Add("Cookie", "glosify.language=Polish");
        var quiz = new HtmlParser().ParseDocument(await _client.GetStringAsync("/preview/quiz/sample"));
        Assert.Contains("Polish essentials", quiz.Body!.TextContent);
        Assert.Contains("cześć", quiz.Body.TextContent);
        Assert.DoesNotContain("Spanish sample", quiz.Body.TextContent);
        var transcript = new HtmlParser().ParseDocument(await _client.GetStringAsync("/preview/transcripts/sample"));
        Assert.Contains("Jak się masz?", transcript.QuerySelector(".transcript-reader")!.TextContent);
        var translated = new HtmlParser().ParseDocument(await _client.GetStringAsync("/preview/transcripts/sample?stream=translation"));
        Assert.Contains("How are you?", translated.QuerySelector(".transcript-reader")!.TextContent);
        var translation = new HtmlParser().ParseDocument(await _client.GetStringAsync("/preview/translations/sample"));
        Assert.Contains("Mam się dobrze.", translation.Body!.TextContent);
        _client.DefaultRequestHeaders.Remove("Cookie");
        _client.DefaultRequestHeaders.Add("Cookie", "glosify.language=Polish; glosify.guest-language=Japanese");
        var switched = new HtmlParser().ParseDocument(await _client.GetStringAsync("/preview/quiz/sample"));
        Assert.Contains("こんにちは", switched.Body!.TextContent);
        Assert.Contains("Japanese sample", switched.Body.TextContent);
        Assert.DoesNotContain("Polish essentials", switched.Body.TextContent);
    }

    [Fact]
    public async Task SelectingLanguageOnSample_ReturnsToSamePageWithNewContent()
    {
        var html = new HtmlParser().ParseDocument(await _client.GetStringAsync("/Languages?returnUrl=/preview/quiz/sample/settings"));
        var token = html.QuerySelector("input[name='__RequestVerificationToken']")!.GetAttribute("value")!;
        var response = await _client.PostAsync("/Languages/Select", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["language"] = "Polish", ["returnUrl"] = "/preview/quiz/sample/settings", ["__RequestVerificationToken"] = token
        }));
        Assert.Equal("/preview/quiz/sample/settings", response.Headers.Location!.OriginalString);
        var page = new HtmlParser().ParseDocument(await _client.GetStringAsync(response.Headers.Location));
        Assert.Contains("Polish essentials", page.Body!.TextContent);
        Assert.Contains("cześć", page.Body.TextContent);
    }

    [Fact]
    public async Task OldSpanishLinks_UseCurrentSelection()
    {
        _client.DefaultRequestHeaders.Add("Cookie", "glosify.guest-language=Polish");
        var page = new HtmlParser().ParseDocument(await _client.GetStringAsync("/preview/quiz/spanish"));
        Assert.Contains("cześć", page.Body!.TextContent);
        Assert.Contains("Polish sample", page.Body.TextContent);
    }

    [Fact]
    public async Task DialogLogin_RendersOnlyFormAndRejectsExternalReturnUrl()
    {
        var response = await _client.GetAsync("/login?dialog=true&returnUrl=https://example.com");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<!DOCTYPE", html, StringComparison.OrdinalIgnoreCase);
        var document = new HtmlParser().ParseDocument(html);
        Assert.Contains("dialog=true", document.QuerySelector(".auth-form")!.GetAttribute("action"));
        Assert.DoesNotContain("example.com", html);
        Assert.NotNull(document.QuerySelector("input[name='__RequestVerificationToken']"));
    }

    [Fact]
    public async Task DialogLogin_InvalidInputReturnsFormErrors()
    {
        var page = await _client.GetStringAsync("/login?dialog=true&returnUrl=/Books");
        var token = new HtmlParser().ParseDocument(page).QuerySelector("input[name='__RequestVerificationToken']")!.GetAttribute("value")!;
        var response = await _client.PostAsync("/login?dialog=true&returnUrl=/Books", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["Email"] = "invalid", ["Password"] = ""
        }));
        response.EnsureSuccessStatusCode();
        var document = new HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync());
        Assert.NotNull(document.QuerySelector(".auth-error"));
        Assert.NotNull(document.QuerySelector(".auth-form"));
    }
}

public sealed class GuestWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Demo:Enabled"] = "false" }));
    }
}
