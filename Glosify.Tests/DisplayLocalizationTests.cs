using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AngleSharp.Html.Parser;
using Glosify.Localization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class DisplayLocalizationTests
{
    public static TheoryData<string, string> SupportedCultures => new()
    {
        { "en-GB", "ltr" },
        { "sv-SE", "ltr" },
        { "es-419", "ltr" },
        { "pt-BR", "ltr" },
        { "fr-FR", "ltr" },
        { "ja-JP", "ltr" },
        { "zh-Hans", "ltr" },
        { "uk-UA", "ltr" },
        { "tr-TR", "ltr" },
        { "id-ID", "ltr" },
        { "vi-VN", "ltr" },
        { "ar", "rtl" },
    };

    public static TheoryData<string, string> LocalizedPublicCultures => new()
    {
        { "sv-SE", "ltr" },
        { "es-419", "ltr" },
        { "pt-BR", "ltr" },
        { "fr-FR", "ltr" },
        { "ja-JP", "ltr" },
        { "zh-Hans", "ltr" },
        { "uk-UA", "ltr" },
        { "tr-TR", "ltr" },
        { "id-ID", "ltr" },
        { "vi-VN", "ltr" },
        { "ar", "rtl" },
    };

    [Fact]
    public async Task First_visit_stays_English_even_when_browser_prefers_Swedish()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("sv-SE,sv;q=0.9");

        var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Equal("en-GB", Assert.Single(response.Content.Headers.ContentLanguage));
        Assert.Contains("lang=\"en-GB\"", html);
        Assert.Contains("Turn new words into real conversations.", html);
    }

    [Fact]
    public async Task Anonymous_selector_sets_standard_cookie_and_renders_Swedish()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        var (token, _) = await AntiForgeryAsync(client, "/login");

        var response = await client.PostAsync("/display-language", new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", token),
            new("culture", "sv-SE"),
            new("returnUrl", "/"),
        ]));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var cultureCookie = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(CookieRequestCultureProvider.DefaultCookieName, StringComparison.Ordinal));
        Assert.Contains("expires=", cultureCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cultureCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cultureCookie, StringComparison.OrdinalIgnoreCase);

        var localized = await client.GetAsync("/");
        var html = await localized.Content.ReadAsStringAsync();
        Assert.Equal("sv-SE", Assert.Single(localized.Content.Headers.ContentLanguage));
        Assert.Contains("lang=\"sv-SE\"", html);
        var document = await new HtmlParser().ParseDocumentAsync(html);
        Assert.Equal("Gör nya ord till riktiga samtal.", document.QuerySelector("#home-title")?.TextContent.Trim());
        var clientText = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
            document.Body?.GetAttribute("data-i18n") ?? "{}");
        Assert.Equal("Något gick fel. Försök igen.", clientText?["Client.GenericError"]);
    }

    [Fact]
    public async Task Account_claim_takes_precedence_over_conflicting_cookie()
    {
        using var factory = AuthenticatedFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            "Cookie",
            $"{CookieRequestCultureProvider.DefaultCookieName}={Uri.EscapeDataString("c=en-GB|uic=en-GB")}");

        var response = await client.GetAsync("/Home/Privacy");
        var html = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Equal("sv-SE", Assert.Single(response.Content.Headers.ContentLanguage));
        Assert.Contains("Integritetspolicy", html);
    }

    [Fact]
    public async Task Selector_rejects_invalid_culture_and_requires_antiforgery()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var missingToken = await client.PostAsync("/display-language", new FormUrlEncodedContent(
        [
            new("culture", "sv-SE"),
            new("returnUrl", "/"),
        ]));
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);

        var (token, cookie) = await AntiForgeryAsync(client, "/login");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/display-language")
        {
            Content = new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", token),
                new("culture", "de-DE"),
                new("returnUrl", "https://example.test/escape"),
            ]),
        };
        request.Headers.Add("Cookie", cookie);
        var invalidCulture = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, invalidCulture.StatusCode);
    }

    [Fact]
    public async Task Selector_falls_back_to_root_for_an_external_return_url()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var (token, cookie) = await AntiForgeryAsync(client, "/login");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/display-language")
        {
            Content = new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", token),
                new("culture", "sv-se"),
                new("returnUrl", "https://example.test/escape"),
            ]),
        };
        request.Headers.Add("Cookie", cookie);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [MemberData(nameof(SupportedCultures))]
    public void Display_culture_catalog_canonicalizes_every_supported_value(string culture, string _)
    {
        Assert.True(DisplayCultureCatalog.TryCanonicalize($" {culture.ToLowerInvariant()} ", out var actual));
        Assert.Equal(culture, actual);
    }

    [Theory]
    [InlineData("en-GB", "en-GB")]
    [InlineData(" sv-se ", "sv-SE")]
    public void Display_culture_catalog_canonicalizes_only_supported_values(string input, string expected)
    {
        Assert.True(DisplayCultureCatalog.TryCanonicalize(input, out var actual));
        Assert.Equal(expected, actual);
        Assert.False(DisplayCultureCatalog.IsSupported("sv-FI"));
    }

    [Fact]
    public void Every_resource_has_exactly_the_neutral_keys_and_matching_placeholders()
    {
        var repositoryRoot = FindRepositoryRoot();
        var resourceDirectory = Path.Combine(repositoryRoot, "Glosify", "Resources");
        var english = RawStrings(Path.Combine(resourceDirectory, "Localization.UiText.resx"));
        foreach (var culture in DisplayCultureCatalog.All.Where(item => item.Name != "en-GB"))
        {
            var localized = RawStrings(Path.Combine(
                resourceDirectory,
                $"Localization.UiText.{culture.Name}.resx"));
            Assert.Equal(english.Keys.Order(), localized.Keys.Order());
            Assert.DoesNotContain(localized, item =>
                item.Value.Contains('Ã')
                || item.Value.Contains('Ð')
                || item.Value.Any(character => character is >= '\u0080' and <= '\u009F'));
            foreach (var key in english.Keys)
            {
                Assert.Equal(Placeholders(english[key]), Placeholders(localized[key]));
            }
        }
    }

    [Theory]
    [MemberData(nameof(LocalizedPublicCultures))]
    public async Task Localized_public_routes_set_culture_cookie_and_emit_complete_seo(string culture, string direction)
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

        var response = await client.GetAsync($"/{culture}/privacy");
        var html = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Equal(culture, Assert.Single(response.Content.Headers.ContentLanguage));
        Assert.Contains($"lang=\"{culture}\"", html);
        Assert.Contains($"dir=\"{direction}\"", html);
        Assert.Contains("legal-language-notice", html);
        Assert.Contains($"href=\"/privacy/english\"", html);
        Assert.Contains($"rel=\"canonical\" href=\"http://localhost/{culture}/privacy\"", html);
        var document = await new HtmlParser().ParseDocumentAsync(html);
        Assert.Equal(13, document.QuerySelectorAll("link[rel='alternate'][hreflang]").Length);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), value =>
            value.StartsWith(CookieRequestCultureProvider.DefaultCookieName, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/sv-SE/privacy")]
    [InlineData("/sv-SE/terms")]
    public async Task Swedish_legal_pages_do_not_describe_retired_features(string path)
    {
        using var factory = CreateFactory();
        var response = await factory.CreateClient().GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.DoesNotContain("klassrum", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("talöv", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Invalid_localized_public_route_is_not_found()
    {
        using var factory = CreateFactory();
        var response = await factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        }).GetAsync("/de-DE/privacy");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Sitemap_contains_each_public_page_in_every_display_culture()
    {
        using var factory = CreateFactory();
        var response = await factory.CreateClient().GetAsync("/sitemap.xml");
        var xml = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(48, System.Xml.Linq.XDocument.Parse(xml).Descendants().Count(element => element.Name.LocalName == "loc"));
        Assert.Contains("http://localhost/ar/terms", xml);
        Assert.Contains("http://localhost/zh-Hans/support", xml);
    }

    [Fact]
    public void Swedish_satellite_resource_is_resolvable()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("sv-SE");
            Assert.Equal(
                "Gör nya ord till riktiga samtal.",
                new UiTextStringLocalizer()["Home.TitleAnonymous"].Value);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    private static Dictionary<string, string> RawStrings(string path) =>
        System.Xml.Linq.XDocument.Load(path)
            .Root!
            .Elements("data")
            .ToDictionary(
                element => element.Attribute("name")!.Value,
                element => element.Element("value")?.Value ?? string.Empty,
                StringComparer.Ordinal);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Glosify.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not find the repository root above test output directory '{AppContext.BaseDirectory}'.");
    }

    private static string[] Placeholders(string value) =>
        System.Text.RegularExpressions.Regex.Matches(value, @"\{\d+(?:[^}]*)?\}")
            .Select(match => match.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static async Task<(string Token, string Cookie)> AntiForgeryAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"GET {url} returned {(int)response.StatusCode}: {body}");
        var document = await new HtmlParser().ParseDocumentAsync(body);
        var token = document.QuerySelector("input[name='__RequestVerificationToken']")?.GetAttribute("value")
            ?? throw new Xunit.Sdk.XunitException("No antiforgery token was rendered.");
        var cookie = string.Join(
            "; ",
            response.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]));
        return (token, cookie);
    }

    private static WebApplicationFactory<Program> AuthenticatedFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = SwedishAuthHandler.TestScheme;
                    options.DefaultChallengeScheme = SwedishAuthHandler.TestScheme;
                    options.DefaultForbidScheme = SwedishAuthHandler.TestScheme;
                }).AddScheme<AuthenticationSchemeOptions, SwedishAuthHandler>(SwedishAuthHandler.TestScheme, _ => { });
            });
        });

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Development"));

    private sealed class SwedishAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "SwedishDisplayCultureTests";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            Claim[] claims =
            [
                new(ClaimTypes.NameIdentifier, "swedish-user"),
                new(DisplayCultureCatalog.ClaimType, DisplayCultureCatalog.SwedishCulture),
            ];
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, TestScheme));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, TestScheme)));
        }
    }
}
