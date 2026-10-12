using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace Glosify.BrowserTests;

public sealed partial class PortfolioJourneys
{
    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task AssistantLearningChanges_RefreshWithoutNavigation_AndProtectUnsavedInput()
    {
        await SetupChatRaceAsync(route => FulfillHistoryAsync(route, "History"));
        await Page.EvaluateAsync("window.learningNavigationSentinel = 'same-document'");
        var version = 0;
        await Page.RouteAsync("**/Quizzes", async route =>
        {
            if (!route.Request.Headers.ContainsKey("x-learning-refresh")) { await route.ContinueAsync(); return; }
            await route.FulfillAsync(new RouteFulfillOptions
            {
                ContentType = "text/html",
                Body = $"<div data-learning-screen data-learning-controller='quiz'><section data-quiz-library><article data-quiz-card>Quiz version {version}</article><form><input name='draft' aria-label='Draft name'></form></section></div>"
            });
        });
        // The runtime contract is covered by AssistantLearningManagementTests; drive its committed event here.
        version = 1;
        await LearningChangeAsync("created");
        await Expect(Page.Locator("[data-quiz-card]")).ToHaveTextAsync("Quiz version 1");
        Assert.Equal("same-document", await Page.EvaluateAsync<string>("window.learningNavigationSentinel"));
        Assert.True(await Page.Locator("[data-assistant-panel]").CountAsync() > 0);
        await Page.GetByLabel("Draft name").FillAsync("Do not discard");
        version = 2;
        await LearningChangeAsync("renamed");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Update view" })).ToBeVisibleAsync();
        await Expect(Page.GetByLabel("Draft name")).ToHaveValueAsync("Do not discard");
        await Expect(Page.Locator("[data-quiz-card]")).ToHaveTextAsync("Quiz version 1");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Update view" }).ClickAsync();
        await Expect(Page.Locator("[data-quiz-card]")).ToHaveTextAsync("Quiz version 2");
        version = 3;
        await LearningChangeAsync("undone");
        await Expect(Page.Locator("[data-quiz-card]")).ToHaveTextAsync("Quiz version 3");
        Assert.Equal("same-document", await Page.EvaluateAsync<string>("window.learningNavigationSentinel"));
    }

    private Task LearningChangeAsync(string revision) => Page.EvaluateAsync("revision => window.dispatchEvent(new CustomEvent('assistant:changes', { detail: { id: 'learning-run', changeRevision: revision, resourceChanges: [] } }))", revision);
}
