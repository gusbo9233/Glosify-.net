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
    public async Task Shared_tts_client_exposes_queue_api_and_preserves_data_attribute_buttons()
    {
        var client = _factory.CreateClient();

        var script = await client.GetStringAsync("/js/tts.js");

        Assert.Contains("window.GlosifyTts = Object.freeze", script, StringComparison.Ordinal);
        Assert.Contains("playQueue: playQueue", script, StringComparison.Ordinal);
        Assert.Contains("stop: stop", script, StringComparison.Ordinal);
        Assert.Contains("closest('[data-tts]')", script, StringComparison.Ordinal);
        Assert.Contains("unsupportedAzureLanguages.add", script, StringComparison.Ordinal);
        Assert.Contains("falling back to browser TTS", script, StringComparison.Ordinal);
        Assert.Contains("&quality=", script, StringComparison.Ordinal);
        Assert.Contains("&voice=", script, StringComparison.Ordinal);
        Assert.Contains("voice: String(item && item.voice", script, StringComparison.Ordinal);
        Assert.Contains("No browser voice was substituted", script, StringComparison.Ordinal);
        Assert.Contains("dataset.ttsLocales", script, StringComparison.Ordinal);
        Assert.DoesNotContain("'danish': 'da-DK'", script, StringComparison.Ordinal);
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
    }

    [Fact]
    public async Task Reader_client_queues_page_or_selection_and_stops_on_rerender()
    {
        var client = _factory.CreateClient();

        var script = await client.GetStringAsync("/js/book-reader.js");

        Assert.Contains("const TTS_CHUNK_LENGTH = 180", script, StringComparison.Ordinal);
        Assert.Contains("const captureReaderSelection", script, StringComparison.Ordinal);
        Assert.Contains("const pdfItemsShareVisualWord", script, StringComparison.Ordinal);
        Assert.Contains("const selectedSourceText", script, StringComparison.Ordinal);
        Assert.Contains("const selectedTextWithinNode", script, StringComparison.Ordinal);
        Assert.Contains("addEventListener('copy'", script, StringComparison.Ordinal);
        Assert.Contains("const sourceSpeechLanguage", script, StringComparison.Ordinal);
        Assert.Contains("window.GlosifyTts.playQueue", script, StringComparison.Ordinal);
        Assert.Contains("quality: 'hd-supported-v2'", script, StringComparison.Ordinal);
        Assert.Contains("voice: voiceForSpeechLanguage(language)", script, StringComparison.Ordinal);
        Assert.Contains("glosify.reader.polishVoice", script, StringComparison.Ordinal);
        Assert.Contains("paintSpeechSegment(item.meta.segmentIndex)", script, StringComparison.Ordinal);
        Assert.Contains("stopReaderTts();", script, StringComparison.Ordinal);
        Assert.Contains("Finished reading page", script, StringComparison.Ordinal);
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
        Assert.Contains("data-reader-voice", view, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Polish reading voice\"", view, StringComparison.Ordinal);
        Assert.Contains("<option value=\"zofia\" selected>Zofia</option>", view, StringComparison.Ordinal);
        Assert.Contains(".reader-highlight-fragment.is-speech", css, StringComparison.Ordinal);
        Assert.Contains(".reader-tts-live", css, StringComparison.Ordinal);
        Assert.Contains(".reader-tts-label", css, StringComparison.Ordinal);
        Assert.Contains(".reader-voice-picker", css, StringComparison.Ordinal);
    }
}
