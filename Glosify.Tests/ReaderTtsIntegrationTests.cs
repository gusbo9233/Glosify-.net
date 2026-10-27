using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Text.Json;
using Xunit;

namespace Glosify.Tests;

public sealed class ReaderTtsIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ReaderTtsIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Shared_tts_client_is_served_with_the_layout_script_contract()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/js/tts.js");
        response.EnsureSuccessStatusCode();
        Assert.Contains("javascript", response.Content.Headers.ContentType?.MediaType ?? "", StringComparison.Ordinal);
        var page = await new HtmlParser().ParseDocumentAsync(await client.GetStringAsync("/"));
        var script = page.QuerySelector("script[src^='/js/tts.js']");
        Assert.NotNull(script);
        Assert.NotEqual("module", script.GetAttribute("type"));
    }

    [Fact]
    public async Task App_layout_publishes_server_owned_TTS_locale_aliases()
    {
        var client = _factory.CreateClient();
        var html = await client.GetStringAsync("/");
        var document = await new HtmlParser().ParseDocumentAsync(html);
        var serializedAliases = document.Body?.GetAttribute("data-tts-locales");

        Assert.False(string.IsNullOrWhiteSpace(serializedAliases));
        var aliases = JsonSerializer.Deserialize<Dictionary<string, string>>(serializedAliases);
        Assert.NotNull(aliases);
        Assert.Equal("da-DK", aliases["danish"]);
        Assert.Equal("sv-SE", aliases["swedish"]);
        Assert.Equal("zh-HK", aliases["cantonese"]);
        var dialog = document.QuerySelector("[data-speech-dialog]");
        Assert.NotNull(dialog);
        Assert.NotNull(document.QuerySelector("[data-speech-settings]"));
        Assert.NotNull(dialog.QuerySelector("[data-speech-save]"));
        Assert.Null(dialog.QuerySelector("[data-speech-play]"));
        // Paid ElevenLabs speech is offered only to signed-in learners; guests get a sign-in link.
        Assert.Equal(new[] { "browser" }, dialog.QuerySelectorAll("[data-speech-provider] option")
            .Select(option => option.GetAttribute("value")));
        Assert.NotNull(dialog.QuerySelector("a[href='/login'][data-login-required]"));
        Assert.False(string.IsNullOrWhiteSpace(dialog.QuerySelector("[data-speech-token] input")?.GetAttribute("value")));
        Assert.Contains("AI credits", dialog.QuerySelector("[data-speech-credits]")!.TextContent, StringComparison.Ordinal);
        Assert.NotNull(dialog.QuerySelector("[data-speech-language] option[value='zh-HK']"));
    }

    [Fact]
    public async Task Signed_in_speech_settings_offer_ElevenLabs()
    {
        using var app = new AvatarFixture(); using var client = await app.Client("learner");
        var document = await new HtmlParser().ParseDocumentAsync(await client.GetStringAsync("/Books"));
        var dialog = document.QuerySelector("[data-speech-dialog]")!;
        Assert.Equal(new[] { "browser", "elevenlabs" }, dialog.QuerySelectorAll("[data-speech-provider] option")
            .Select(option => option.GetAttribute("value")));
        Assert.Null(dialog.QuerySelector("a[href='/login'][data-login-required]"));
    }

    [Fact]
    public async Task Reader_client_is_delivered_as_a_module_on_the_reader_page()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/js/book-reader.js");
        response.EnsureSuccessStatusCode();
        Assert.Contains("javascript", response.Content.Headers.ContentType?.MediaType ?? "", StringComparison.Ordinal);
        var document = await new HtmlParser().ParseDocumentAsync(await client.GetStringAsync("/preview/books/sample"));
        var script = document.QuerySelector("script[src^='/js/book-reader.js']");
        Assert.NotNull(script);
        Assert.Equal("module", script.GetAttribute("type"));
    }

    [Fact]
    public async Task Reader_view_and_styles_include_accessible_responsive_controls()
    {
        var viewPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../Glosify/Views/Books/Read.cshtml"));
        Assert.True(File.Exists(viewPath), $"Reader view was not found at {viewPath}.");
        var view = await File.ReadAllTextAsync(viewPath);
        var client = _factory.CreateClient();
        var css = await client.GetStringAsync("/css/site.css");

        Assert.Contains("data-reading-language", view, StringComparison.Ordinal);
        Assert.Contains("data-reader-tts", view, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Read this page aloud\"", view, StringComparison.Ordinal);
        Assert.Contains("data-reader-tts-status aria-live=\"polite\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("data-reader-voice", view, StringComparison.Ordinal);
        Assert.Contains(".reader-highlight-fragment.is-speech", css, StringComparison.Ordinal);
        Assert.Contains(".reader-tts-live", css, StringComparison.Ordinal);
        Assert.Contains(".reader-tts-label", css, StringComparison.Ordinal);
        Assert.Contains(".reader-voice-picker", css, StringComparison.Ordinal);
    }
}
