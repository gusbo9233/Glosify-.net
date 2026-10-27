using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace Glosify.BrowserTests;

public sealed partial class PortfolioJourneys
{
    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task AnkiSettingsUpdateWithoutReloadingThePage()
    {
        await RegisterAndSelectPolishAsync();
        await CreateQuizWithWordAsync();
        await Page.WaitForLoadStateAsync(LoadState.Load);
        await Page.EvaluateAsync("localStorage.setItem('glosify.anki.settingsOpen', 'false')");
        await Page.GotoAsync("/Anki");
        await Page.WaitForLoadStateAsync(LoadState.Load);
        await Page.EvaluateAsync("window.ankiDocumentMarker = 'same-document'");
        var settings = Page.Locator("[data-anki-settings]");
        await Expect(settings).ToBeVisibleAsync();

        await settings.GetByLabel("Quiz", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Portfolio Polish" });
        var response = await Page.RunAndWaitForResponseAsync(
            () => settings.GetByRole(AriaRole.Button, new() { Name = "Add to Anki", Exact = true }).ClickAsync(),
            response => response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath == "/Anki/AddQuiz");
        Assert.Equal(200, response.Status);
        await Expect(settings.Locator("[data-anki-request-status]")).ToHaveTextAsync("Quiz added to Anki.");
        await Expect(settings).ToBeVisibleAsync();
        await Expect(settings.Locator("[data-anki-item-key]:checked")).ToHaveCountAsync(1);

        // Searching the settings must keep the currently revealed study card.
        await Page.GetByRole(AriaRole.Button, new() { Name = "Reveal answer for house", Exact = true }).ClickAsync();
        await Expect(Page.Locator(".flashcard.is-revealed")).ToBeVisibleAsync();
        await settings.GetByRole(AriaRole.Searchbox).FillAsync("dom");
        await settings.GetByRole(AriaRole.Button, new() { Name = "Search", Exact = true }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("search=dom"));
        await Expect(Page.Locator(".flashcard.is-revealed")).ToBeVisibleAsync();

        await settings.Locator("[data-anki-item-key]").UncheckAsync();
        await settings.GetByRole(AriaRole.Button, new() { Name = "Save 1 change", Exact = true }).ClickAsync();
        await Expect(settings.Locator("[data-anki-request-status]")).ToHaveTextAsync("Removed 1 item from Anki.");
        await Expect(settings.Locator("[data-anki-item-key]:checked")).ToHaveCountAsync(0);
        await settings.Locator("[data-anki-item-key]").CheckAsync();
        await settings.GetByRole(AriaRole.Button, new() { Name = "Save 1 change", Exact = true }).ClickAsync();
        await Expect(settings.Locator("[data-anki-request-status]")).ToHaveTextAsync("Added 1 item to Anki.");

        await settings.GetByRole(AriaRole.Button, new() { Name = "Remove Portfolio Polish from Anki", Exact = true }).ClickAsync();
        await Expect(settings.Locator("[data-anki-request-status]")).ToHaveTextAsync("Quiz removed from Anki.");
        Assert.Equal("same-document", await Page.EvaluateAsync<string>("window.ankiDocumentMarker"));
        await AssertNoPageErrorsAsync();
    }
}
