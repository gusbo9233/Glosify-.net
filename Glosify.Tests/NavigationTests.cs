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

    [Theory]
    [InlineData("/Quizzes")]
    [InlineData("/Quizzes/Index")]
    [InlineData("/Quizzes/Settings")]
    [InlineData("/Languages")]
    [InlineData("/Explore")]
    [InlineData("/FlashcardQuiz")]
    [InlineData("/TypingQuiz")]
    [InlineData("/Speaking")]
    [InlineData("/CustomQuizzes/00000000-0000-0000-0000-000000000001/Play")]
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
            "form-action 'self' https://glosify-f0d9e2g3f4ctc3hy.swedencentral-01.azurewebsites.net",
            csp);
        Assert.Contains("https://accounts.google.com", csp);
        Assert.Contains("https://login.microsoftonline.com", csp);
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

        Assert.Contains(hrefs, h => h.Equals("/", StringComparison.Ordinal) || h.Contains("/Home", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(hrefs, h => h.Contains("/Languages", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(hrefs, h => h.Contains("/Quiz", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<(string form, string cookie)> AntiForgeryAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        var parser = new HtmlParser();
        var doc = await parser.ParseDocumentAsync(html);
        var token = doc.QuerySelector("input[name='__RequestVerificationToken']")!.GetAttribute("value")!;

        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var sc) ? sc : Enumerable.Empty<string>();
        var cookie = string.Join("; ", setCookies.Select(c => c.Split(';')[0]));
        return (token, cookie);
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
