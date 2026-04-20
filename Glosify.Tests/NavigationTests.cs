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
    [InlineData("/Login")]
    [InlineData("/Login/Index")]
    [InlineData("/Quizzes")]
    [InlineData("/Quizzes/Index")]
    [InlineData("/Quizzes/Settings")]
    [InlineData("/Quizzes/Flashcard")]
    [InlineData("/Quizzes/Type")]
    public async Task Get_NavigableRoute_ReturnsSuccess(string url)
    {
        var client = CreateClient();

        var response = await client.GetAsync(url);

        response.EnsureSuccessStatusCode();
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task Home_RendersSidebarWithExpectedLinks()
    {
        var client = CreateClient();

        var document = await GetDocumentAsync(client, "/Home/Index");

        var aside = document.QuerySelector("aside");
        Assert.NotNull(aside);

        var hrefs = aside!.QuerySelectorAll("a")
            .Select(a => a.GetAttribute("href"))
            .Where(h => !string.IsNullOrEmpty(h))
            .ToArray();

        Assert.Contains(hrefs, h => h == "/" || h!.Contains("/Home", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Home_MarksHomeLinkAsActive()
    {
        var client = CreateClient();

        var document = await GetDocumentAsync(client, "/Home/Index");

        var activeLinks = document.QuerySelectorAll("aside a")
            .Where(a => (a.GetAttribute("class") ?? "").Contains("border-l-4"))
            .ToArray();

        Assert.Single(activeLinks);
        Assert.Contains("Home", activeLinks[0].TextContent);
    }

    [Fact]
    public async Task Login_FormSubmitsToHomeIndex()
    {
        var client = CreateClient();

        var document = await GetDocumentAsync(client, "/Login");

        var form = document.QuerySelector("form[method='get']") as IHtmlFormElement;
        Assert.NotNull(form);

        var action = form!.GetAttribute("action") ?? "";
        Assert.True(action == "/" || action.Contains("/Home", StringComparison.OrdinalIgnoreCase),
            $"Expected form action to point to Home, got '{action}'");
    }

    [Fact]
    public async Task Login_SubmittingLoginForm_NavigatesToHome()
    {
        var client = CreateClient();
        var document = await GetDocumentAsync(client, "/Login");
        var form = (IHtmlFormElement)document.QuerySelector("form[method='get']")!;

        var submitUrl = form.GetAttribute("action") ?? "/Home/Index";
        var response = await client.GetAsync(submitUrl + "?Email=test@test.com&Password=whatever");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Sonic Polyglot", body);
    }

    [Fact]
    public async Task Home_SidebarContainsAllExpectedNavItems()
    {
        var client = CreateClient();
        var document = await GetDocumentAsync(client, "/");

        var linkTexts = document.QuerySelectorAll("aside a")
            .Select(a => a.TextContent.Trim())
            .ToArray();

        string[] expected =
        {
            "Language selector",
            "Home",
            "Explore",
            "Dictionary",
            "Practice",
            "Library",
            "Quizzes"
        };

        foreach (var item in expected)
        {
            Assert.Contains(linkTexts, t => t.Contains(item));
        }
    }

    [Fact]
    public async Task Quizzes_Index_LinksToSettings()
    {
        var client = CreateClient();
        var document = await GetDocumentAsync(client, "/Quizzes");

        var hrefs = document.QuerySelectorAll("a")
            .Select(a => a.GetAttribute("href") ?? "")
            .ToArray();

        Assert.Contains(hrefs, h => h.Contains("/Quizzes/Settings", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Quizzes_Settings_FormPostsToStart()
    {
        var client = CreateClient();
        var document = await GetDocumentAsync(client, "/Quizzes/Settings");

        var form = (IHtmlFormElement?)document.QuerySelector("form[method='post']");
        Assert.NotNull(form);
        Assert.Contains("/Quizzes/Start", form!.GetAttribute("action") ?? "", StringComparison.OrdinalIgnoreCase);

        Assert.NotNull(document.QuerySelector("input[name='mode'][value='flashcards']"));
        Assert.NotNull(document.QuerySelector("input[name='mode'][value='typing']"));
    }

    [Theory]
    [InlineData("flashcards", "/Quizzes/Flashcard")]
    [InlineData("typing", "/Quizzes/Type")]
    public async Task Quizzes_Start_RedirectsToSelectedMode(string mode, string expectedLocation)
    {
        var client = CreateClient();
        var tokens = await AntiForgeryAsync(client, "/Quizzes/Settings");

        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("mode", mode),
            new KeyValuePair<string, string>("__RequestVerificationToken", tokens.form)
        });
        var request = new HttpRequestMessage(HttpMethod.Post, "/Quizzes/Start") { Content = content };
        request.Headers.Add("Cookie", tokens.cookie);

        var response = await client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(expectedLocation, response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Quizzes_Flashcard_RendersShowAnswerButton()
    {
        var client = CreateClient();
        var document = await GetDocumentAsync(client, "/Quizzes/Flashcard");
        Assert.Contains(document.QuerySelectorAll("button"), b => b.TextContent.Contains("Show Answer"));
    }

    [Fact]
    public async Task Quizzes_Type_RendersAnswerInput()
    {
        var client = CreateClient();
        var document = await GetDocumentAsync(client, "/Quizzes/Type");
        Assert.NotNull(document.QuerySelector("input[name='answer']"));
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
