using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Glosify.Tests;

public class NavigationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public NavigationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false
    });

    [Theory]
    [InlineData("/")]
    [InlineData("/Home")]
    [InlineData("/Home/Index")]
    [InlineData("/Home/Privacy")]
    [InlineData("/Home/Terms")]
    [InlineData("/Home/Support")]
    [InlineData("/login")]
    [InlineData("/Account/Login")]
    [InlineData("/Account/Register")]
    public async Task Get_AnonymousRoute_ReturnsHtml(string url)
    {
        var client = CreateClient();

        var response = await client.GetAsync(url);

        response.EnsureSuccessStatusCode();
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task PublicAuthAndLegalPagesExposeTheStoreDisclosuresAndWorkingLinks()
    {
        var client = CreateClient();
        var login = await (await client.GetAsync("/login")).Content.ReadAsStringAsync();
        var register = await (await client.GetAsync("/Account/Register?returnUrl=%2Fextension%2Fconnect%3Fstate%3Dtest")).Content.ReadAsStringAsync();
        var privacy = await (await client.GetAsync("/Home/Privacy")).Content.ReadAsStringAsync();
        var terms = await (await client.GetAsync("/Home/Terms")).Content.ReadAsStringAsync();
        var support = await (await client.GetAsync("/Home/Support")).Content.ReadAsStringAsync();

        Assert.Contains("receive 25 credits once when you sign in with Google or Microsoft", login);
        Assert.Contains("Password accounts do not receive automatic trial credits", login);
        Assert.Contains("25-credit trial", register);
        Assert.Contains("returnUrl=%2Fextension%2Fconnect%3Fstate%3Dtest", register);
        Assert.DoesNotContain("href=\"#\"", login);
        Assert.DoesNotContain("href=\"#\"", register);
        Assert.Contains("Chrome Web Store Limited Use", privacy);
        Assert.Contains("complete effective model request", privacy);
        Assert.Contains("Transcript saving is off by default", privacy);
        Assert.Contains("provider reports token or audio usage", terms);
        Assert.Contains("mandatory consumer rights", terms);
        Assert.Contains("AI-generated replies", terms);
        Assert.Contains("Do not send passwords", support);
    }

    [Fact]
    public async Task ApplicationLayoutKeepsLegalPagesAccessible()
    {
        var document = await GetDocumentAsync(CreateClient(), "/");
        var footer = document.QuerySelector("footer.app-legal-footer");

        Assert.NotNull(footer);
        Assert.NotNull(footer!.QuerySelector("a[href='/Home/Privacy']"));
        Assert.NotNull(footer.QuerySelector("a[href='/Home/Terms']"));
        Assert.NotNull(footer.QuerySelector("a[href='/Home/Support']"));
    }

    [Theory]
    [InlineData("/Quizzes")]
    [InlineData("/Quizzes/Index")]
    [InlineData("/Quizzes/Settings")]
    [InlineData("/Languages")]
    [InlineData("/Explore")]
    [InlineData("/FlashcardQuiz")]
    [InlineData("/TypingQuiz")]
    [InlineData("/Anki")]
    [InlineData("/Admin/AiCredits")]
    public async Task Get_AuthorizedRoute_RedirectsToLoginWhenAnonymous(string url)
    {
        var client = CreateClient();

        var response = await client.GetAsync(url);

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location ?? throw new Xunit.Sdk.XunitException("No redirect location");
        var path = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString.Split('?')[0];
        var query = location.IsAbsoluteUri ? location.Query : (location.OriginalString.Contains('?') ? location.OriginalString[location.OriginalString.IndexOf('?')..] : string.Empty);
        Assert.Equal("/login", path, ignoreCase: true);
        Assert.Contains("ReturnUrl", query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_RendersFormBoundToLoginViewModel()
    {
        var client = CreateClient();

        var document = await GetDocumentAsync(client, "/login");

        var form = document.QuerySelector("form[method='post']") as IHtmlFormElement;
        Assert.NotNull(form);

        var action = form!.GetAttribute("action") ?? string.Empty;
        // The named "login" route maps `/login` to AccountController.Login, so the URL helper
        // should emit a path that resolves to the same place — either `/login` or `/Account/Login`.
        Assert.True(
            action.Contains("/login", StringComparison.OrdinalIgnoreCase)
            || action.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase),
            $"Login form action does not point at the login endpoint: '{action}'");
        Assert.NotNull(document.QuerySelector("input[name='Email']"));
        Assert.NotNull(document.QuerySelector("input[name='Password']"));
        Assert.NotNull(document.QuerySelector("input[name='__RequestVerificationToken']"));
    }

    [Fact]
    public async Task Login_WithExternalLoginError_RendersUsefulMessage()
    {
        var client = CreateClient();

        var document = await GetDocumentAsync(client, "/login?externalLoginError=Google");

        Assert.Contains(
            "Google login failed. Check the local Google OAuth client ID and client secret, then try again.",
            document.Body?.TextContent ?? string.Empty);
    }

    [Fact]
    public async Task Login_AllowsConfiguredExternalLoginFormActionOriginInCsp()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/login");

        response.EnsureSuccessStatusCode();
        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains(
            "form-action 'self' https://glosify-app.azurewebsites.net",
            csp);
        Assert.Contains("https://accounts.google.com", csp);
        Assert.Contains("https://login.microsoftonline.com", csp);
        var formAction = csp
            .Split(';')
            .Select(directive => directive.Trim())
            .Single(directive => directive.StartsWith("form-action ", StringComparison.Ordinal));
        var formActionSources = formAction.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("https://checkout.stripe.com", formActionSources);
    }

    [Fact]
    public async Task ResponsesKeepTtsObjectUrlsButRemoveRetiredBrowserCapabilities()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/login");

        response.EnsureSuccessStatusCode();
        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("media-src 'self' blob:", csp);
        Assert.Contains("connect-src 'self';", csp);
        Assert.Contains("worker-src 'self';", csp);
        Assert.DoesNotContain("jsdelivr", csp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("communication.azure.com", csp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("speech.microsoft.com", csp, StringComparison.OrdinalIgnoreCase);

        var permissions = Assert.Single(response.Headers.GetValues("Permissions-Policy"));
        Assert.Contains("microphone=()", permissions);
        Assert.Contains("camera=()", permissions);
    }

    [Fact]
    public async Task Post_WithoutAntiForgeryToken_IsRejected()
    {
        var client = CreateClient();

        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Email", "user@example.test"),
            new KeyValuePair<string, string>("Password", "irrelevant")
        });

        var response = await client.PostAsync("/Account/Login", content);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Home_RendersSidebarWithExpectedNavLinks()
    {
        var client = CreateClient();
        var document = await GetDocumentAsync(client, "/");

        var hrefs = document.QuerySelectorAll("aside a")
            .Select(a => a.GetAttribute("href") ?? string.Empty)
            .Where(h => h.Length > 0)
            .ToArray();

        Assert.NotNull(document.QuerySelector("aside .app-sidebar-brand .wordmark a[href='/']"));
        Assert.Contains(hrefs, h => h.Equals("/", StringComparison.Ordinal) || h.Contains("/Home", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(hrefs, h => h.Contains("/Languages", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(hrefs, h => h.Contains("/Quiz", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(hrefs, h => h.Contains("/Anki", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Home_RendersLearningJourneyWithWorkingAnonymousCallsToAction()
    {
        var client = CreateClient();
        var document = await GetDocumentAsync(client, "/");

        Assert.Equal(
            "Turn new words into real conversations.",
            document.QuerySelector("#home-title")?.TextContent.Trim());
        Assert.NotNull(document.QuerySelector("a[href*='/Account/Register']"));
        Assert.NotNull(document.QuerySelector("a[href='/login'], a[href*='/Account/Login']"));
        Assert.NotNull(document.QuerySelector("a[href*='/Quiz']"));
        Assert.NotNull(document.QuerySelector("a[href*='/Books']"));
        Assert.NotNull(document.QuerySelector("a[href*='/Explore']"));
        Assert.NotNull(document.QuerySelector("a[href*='/Anki']"));
        Assert.Null(document.QuerySelector("a[href*='/Speaking']"));
        Assert.Null(document.QuerySelector("a[href*='/Classroom']"));
    }

    [Fact]
    public async Task Home_LinksUseTheCanonicalLandingRoute()
    {
        var client = CreateClient();
        var document = await GetDocumentAsync(client, "/");

        var homeLinks = document.QuerySelectorAll("a[href='/']");

        Assert.NotEmpty(homeLinks);
        Assert.All(homeLinks, link => Assert.Equal("/", link.GetAttribute("href")));
    }

    private static async Task<AngleSharp.Dom.IDocument> GetDocumentAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        var parser = new HtmlParser();
        return await parser.ParseDocumentAsync(html);
    }
}
