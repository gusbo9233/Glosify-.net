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
            // The Books sample renders its cover from a PDF fetch that leaving early would abort.
            await WaitForNetworkQuiescenceAsync();
        }
        await Page.GotoAsync("/preview/quiz/spanish/practice/typing");
        await Page.Locator("[data-answer-input]").FillAsync("hola");
        await Page.Locator("[data-check-button]").ClickAsync();
        await Expect(Page.Locator("[data-feedback]")).ToHaveTextAsync("Correct!");
        await Page.GetByRole(AriaRole.Link, new() { Name = "Sign in to create your own" }).ClickAsync();
        await Expect(Page.Locator("[data-login-dialog]")).ToBeVisibleAsync();
        // Closing aborts a pending form fetch, so dismiss only after the form has loaded.
        await Expect(Page.Locator("[data-login-dialog] .auth-form")).ToBeVisibleAsync();
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
        await Expect(Page.Locator(".book-title").Filter(new() { HasText = "First words in Spanish" })).ToBeVisibleAsync();
        await WaitForNetworkQuiescenceAsync(); // Signing in navigates away from the cover's PDF fetch.
        await Page.Locator(".books-upload-button").ClickAsync();
        var dialog = Page.Locator("[data-login-dialog]");
        await dialog.GetByLabel("Email Address").FillAsync(email);
        await dialog.GetByLabel("Password", new() { Exact = true }).FillAsync(password);
        await dialog.Locator(".auth-form button[type=submit]").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("/Books$", RegexOptions.IgnoreCase));
        await Expect(Page.Locator("[data-guest-return]")).ToHaveCountAsync(0);
        await Expect(Page.Locator(".app-navigation-identity")).ToContainTextAsync(email);
        // The bundled sample is never copied into the signed-in library.
        await Expect(Page.Locator("a[href^='/preview/']")).ToHaveCountAsync(0);
        await Expect(Page.Locator(".book-title").Filter(new() { HasText = "First words in Spanish" })).ToHaveCountAsync(0);
    }

    [BrowserFact]
    public async Task GuestPracticeShowsOneRestartControl()
    {
        foreach (var mode in new[] { "typing", "flashcards" })
        {
            await Page.GotoAsync($"/preview/quiz/sample/practice/{mode}");
            await Expect(Page.Locator("[data-sample-progress]")).ToContainTextAsync("Card 1 of 5");
            await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Restart full quiz" })).ToHaveCountAsync(1);
        }
        await Page.GotoAsync("/preview/anki/sample");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Restart all cards" })).ToHaveCountAsync(1);
    }

    [BrowserFact]
    public async Task GuestReaderLanguageChoiceStaysLocal()
    {
        await Page.GotoAsync("/preview/books/sample");
        await Page.Locator("[data-translation-language]").SelectOptionAsync("German");
        await Expect(Page.Locator("[data-translation-heading-language]")).ToHaveTextAsync("German");
        await Expect(Page.Locator("[data-login-dialog]")).ToBeHiddenAsync();
        await Expect(Page.GetByText("The language preference could not be saved.")).ToHaveCountAsync(0);
    }
}
