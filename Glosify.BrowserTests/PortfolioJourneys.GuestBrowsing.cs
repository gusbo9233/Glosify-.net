using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Glosify.BrowserTests;

public sealed partial class PortfolioJourneys
{
    [BrowserFact]
    public async Task GuestCanBrowsePracticeDismissLoginAndSignInToOwnLibrary()
    {
        foreach (var section in new[] { "Books", "Quiz", "Anki", "Transcripts", "Translations" })
        {
            await Page.GotoAsync($"/{section}");
            await Expect(Page.Locator("[data-guest-return]")).ToBeVisibleAsync();
            await Expect(Page.Locator(".guest-preview")).ToHaveCountAsync(0);
        }
        await Page.GotoAsync("/preview/quiz/spanish/practice/typing");
        await Page.Locator("[data-answer-input]").FillAsync("hola");
        await Page.Locator("[data-check-button]").ClickAsync();
        await Expect(Page.Locator("[data-feedback]")).ToHaveTextAsync("Correct!");
        await Page.GetByRole(AriaRole.Link, new() { Name = "Sign in to create your own" }).ClickAsync();
        await Expect(Page.Locator("[data-login-dialog]")).ToBeVisibleAsync();
        await Page.Keyboard.PressAsync("Escape");
        await Expect(Page.Locator("[data-feedback]")).ToHaveTextAsync("Correct!");
        await Page.Locator("[data-check-button]").ClickAsync();
        await Expect(Page.Locator("[data-sample-progress]")).ToContainTextAsync("Card 2 of 5");
        await Page.ReloadAsync();
        await Expect(Page.Locator("[data-sample-progress]")).ToContainTextAsync("Card 1 of 5");
        await Page.GotoAsync("/Anki");
        for (var index = 0; index < 5; index++)
        {
            await Page.Locator("[data-sample-reveal]").ClickAsync();
            await Page.Locator("button[name=rating][value=good]").ClickAsync();
        }
        await Expect(Page.Locator("[data-sample-progress]")).ToHaveTextAsync("Complete · 5 of 5 correct");

        var email = $"guest-{Guid.NewGuid():N}@example.test";
        const string password = "GuestPreview!123";
        await SeedExistingAccountAsync(email, password);
        await Page.GotoAsync("/Books");
        await Page.Locator(".books-upload-button").ClickAsync();
        var dialog = Page.Locator("[data-login-dialog]");
        await dialog.GetByLabel("Email Address").FillAsync(email);
        await dialog.GetByLabel("Password", new() { Exact = true }).FillAsync(password);
        await dialog.Locator(".auth-form button[type=submit]").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("/Books$", RegexOptions.IgnoreCase));
        await Expect(Page.Locator("[data-guest-return]")).ToHaveCountAsync(0);
        await Expect(Page.Locator(".app-navigation-identity")).ToContainTextAsync(email);
        await Expect(Page.GetByText("Una mañana en Madrid", new() { Exact = true })).ToHaveCountAsync(0);
    }
}
