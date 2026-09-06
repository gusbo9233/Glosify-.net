using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace Glosify.BrowserTests;

public sealed partial class PortfolioJourneys
{
    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task SentenceModeClearsHiddenWordSelectionAndRestoresRangeControls()
    {
        await RegisterAndSelectPolishAsync();
        await CreateQuizWithWordAsync();
        await Page.GetByLabel("Word").FillAsync("kot");
        await Page.GetByLabel("Translation").FillAsync("cat");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Add word" }).ClickAsync();
        await Expect(Page.Locator(".word-card")).ToHaveCountAsync(2);
        await Page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("Start Quiz", RegexOptions.IgnoreCase) }).ClickAsync();
        await Page.Locator("[data-open-word-picker]").ClickAsync();
        await Page.Locator("[data-word-picker-checkbox]").First.UncheckAsync();
        await Page.Locator("[data-apply-word-picker]").ClickAsync();
        await Expect(Page.Locator("[data-selected-word-ids]")).Not.ToHaveValueAsync("");
        await Expect(Page.Locator("[data-range-min]")).ToBeDisabledAsync();

        await Page.Locator("label.choice").Filter(new()
        {
            Has = Page.Locator("input[name='PracticeItemType'][value='sentences']")
        }).ClickAsync();
        await Expect(Page.Locator("[data-word-picker-row]")).ToBeHiddenAsync();
        await Expect(Page.Locator("[data-selected-word-ids]")).ToHaveValueAsync("");
        await Expect(Page.Locator("[data-selected-word-ids]")).ToBeDisabledAsync();
        await Expect(Page.Locator("[data-range-min]")).ToBeEnabledAsync();
        await Expect(Page.Locator("[data-range-max]")).ToBeEnabledAsync();
        Assert.Null(await Page.Locator("[data-selected-word-ids]").EvaluateAsync<string?>(
            "input => new FormData(input.form).get('SelectedWordIds')"));

        await Page.Locator("label.choice").Filter(new()
        {
            Has = Page.Locator("input[name='PracticeItemType'][value='words']")
        }).ClickAsync();
        await Expect(Page.Locator("[data-selected-word-ids]")).ToBeEnabledAsync();
        await Expect(Page.Locator("[data-selected-word-ids]")).ToHaveValueAsync("");
        await Expect(Page.Locator("[data-word-picker-summary]")).ToBeHiddenAsync();
        await Page.Locator("[data-open-word-picker]").ClickAsync();
        await Expect(Page.Locator("[data-word-picker-checkbox]:checked")).ToHaveCountAsync(2);
        await Page.Locator("[data-close-word-picker]").ClickAsync();
    }
}
