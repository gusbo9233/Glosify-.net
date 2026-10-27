using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;
using Xunit;

namespace Glosify.BrowserTests;

public sealed partial class PortfolioJourneys
{
    [BrowserFact]
    public async Task AvatarModelsLoadThroughThePublishedDecoderAndSwitchWithoutStartingVoice()
    {
        await RegisterAndSelectPolishAsync();
        // Exercise the actual renderer and worker with voice services unavailable.
        await Page.RouteAsync("**/api/avatar/config", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = """{"available":false,"language":"pl","languageName":"Polish","quizzes":[],"autoSaveConversations":false}"""
        }));
        var decoderRequested = false;
        var captureRequested = false;
        Page.Request += (_, request) =>
        {
            decoderRequested |= request.Url.Contains("/js/avatar/meshopt.worker.js", StringComparison.Ordinal);
            captureRequested |= request.Url.Contains("/js/avatar/capture.worklet.js", StringComparison.Ordinal);
        };
        await Page.GotoAsync("/Avatar");
        await Expect(Page.Locator("#avatar-render-status")).ToBeHiddenAsync(new() { Timeout = 60000 });
        Assert.True(decoderRequested, "The model must use the published same-origin decoder worker.");
        Assert.True(await Page.Locator("#avatar-canvas").EvaluateAsync<bool>("canvas => canvas.width > 0 && canvas.height > 0"));
        await Page.Locator("#avatar-character").SelectOptionAsync("rowan");
        await Expect(Page.Locator("#avatar-render-status")).ToBeHiddenAsync(new() { Timeout = 60000 });
        await Expect(Page.Locator("#avatar-canvas")).ToHaveAttributeAsync("aria-label", "Rowan, your animated conversation partner");
        Assert.False(captureRequested, "Loading and switching a portrait must not open microphone capture.");
        if (_settings.TraceDirectory is { } directory)
            await Page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(directory, "avatar-renderer-desktop.png"), Animations = ScreenshotAnimations.Disabled });
        await AssertNoPageErrorsAsync();
    }
}
