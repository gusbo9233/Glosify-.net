using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Glosify.Tests;

/// <summary>
/// Guards the page-delivery details a Lighthouse run flagged: an uncompressed
/// HTML document, web fonts fetched through a chain of requests, and a missing
/// meta description.
/// </summary>
public class PageDeliveryTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public PageDeliveryTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false
    });

    [Theory]
    [InlineData("/")]
    [InlineData("/login")]
    public async Task Pages_are_compressed_for_clients_that_accept_it(string url)
    {
        var client = CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.AcceptEncoding.ParseAdd("br, gzip");

        var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        var encoding = Assert.Single(response.Content.Headers.ContentEncoding);
        Assert.Equal("br", encoding);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/login")]
    public async Task Pages_describe_themselves_for_search_results(string url)
    {
        var document = await GetDocumentAsync(url);

        var description = document.QuerySelector("meta[name='description']")?.GetAttribute("content");

        Assert.False(string.IsNullOrWhiteSpace(description));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/login")]
    public async Task Pages_request_every_web_font_up_front_from_the_head(string url)
    {
        var document = await GetDocumentAsync(url);

        var fontStylesheets = document.QuerySelectorAll("head link[rel='stylesheet']")
            .Select(link => link.GetAttribute("href") ?? string.Empty)
            .Where(href => href.Contains("fonts.googleapis.com", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // Text and icons are two requests, not a chain, and not one request:
        // they need different font-display behavior.
        Assert.Equal(2, fontStylesheets.Length);
        var text = Assert.Single(fontStylesheets, href => href.Contains("family=Lora", StringComparison.Ordinal));
        Assert.Contains("family=Plus+Jakarta+Sans", text, StringComparison.Ordinal);
        Assert.Contains("display=swap", text, StringComparison.Ordinal);

        var icons = Assert.Single(
            fontStylesheets,
            href => href.Contains("family=Material+Symbols+Outlined", StringComparison.Ordinal));
        Assert.Contains("icon_names=", icons, StringComparison.Ordinal);
        Assert.Contains("display=block", icons, StringComparison.Ordinal);

        var preconnects = document.QuerySelectorAll("link[rel='preconnect']")
            .Select(link => link.GetAttribute("href") ?? string.Empty)
            .ToArray();

        Assert.Contains("https://fonts.googleapis.com", preconnects);
        Assert.Contains("https://fonts.gstatic.com", preconnects);
    }

    [Fact]
    public async Task Fingerprinted_assets_are_cached_forever_rather_than_revalidated()
    {
        var client = CreateClient();
        var document = await GetDocumentAsync("/");

        var assets = document.QuerySelectorAll("link[rel='stylesheet'], script[src]")
            .Select(element => element.GetAttribute("href") ?? element.GetAttribute("src") ?? string.Empty)
            .Where(url => url.Contains("?v=", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(assets);

        // Every browser sends Accept-Encoding, and MapStaticAssets answers those
        // with a separate representation carrying its own ETag over the
        // compressed bytes. Asking for identity here would test the one case no
        // real client hits.
        foreach (var asset in assets)
        {
            foreach (var encoding in new[] { "br, gzip", "identity" })
            {
                var request = new HttpRequestMessage(HttpMethod.Get, asset);
                request.Headers.AcceptEncoding.ParseAdd(encoding);

                var response = await client.SendAsync(request);

                response.EnsureSuccessStatusCode();

                // Guards the guard: if the host stopped answering with a
                // compressed representation, the loop above would pass without
                // ever exercising the case that regressed.
                if (encoding != "identity")
                {
                    Assert.NotEmpty(response.Content.Headers.ContentEncoding);
                }

                var cacheControl = response.Headers.CacheControl;
                Assert.True(
                    cacheControl?.MaxAge == TimeSpan.FromDays(365)
                    && cacheControl.Extensions.Any(extension => extension.Name == "immutable"),
                    $"{asset} came back as '{cacheControl}' for Accept-Encoding '{encoding}', "
                    + "so browsers revalidate it on every page view.");
            }
        }
    }

    [Fact]
    public async Task An_asset_whose_fingerprint_does_not_match_is_still_revalidated()
    {
        // A `v` left over from an earlier deploy names bytes the server no longer
        // has, so the response it does return must not be cached under that URL.
        var client = CreateClient();

        var response = await client.GetAsync("/css/site.css?v=Sn4CIzuZjNBTMPGXvxjGWA0ZFyCX4Wg8dJDBaLLGSNo");

        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoCache);
    }

    [Fact]
    public async Task Site_stylesheet_does_not_chain_a_font_request_behind_itself()
    {
        var client = CreateClient();

        var css = await client.GetStringAsync("/css/site.css");

        // An @import is a request the browser can only start once site.css has
        // arrived, which is exactly the serialization the font <link> avoids.
        var withoutComments = Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        Assert.DoesNotContain("@import", withoutComments, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<AngleSharp.Dom.IDocument> GetDocumentAsync(string url)
    {
        var client = CreateClient();
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        return await new HtmlParser().ParseDocumentAsync(html);
    }
}
