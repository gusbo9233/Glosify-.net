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
    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task SelectedFlashcardResetRetainsSelectionAndLeavesGenericSessionIntact()
    {
        await RegisterAndSelectPolishAsync();
        await CreateQuizWithWordAsync();
        await Page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("Start Quiz", RegexOptions.IgnoreCase) }).ClickAsync();
        var quizId = await Page.Locator("input[name='QuizId']").InputValueAsync();
        var wordId = await Page.Locator("[data-word-picker-checkbox]").First.InputValueAsync();
        var genericUrl = $"/FlashcardQuiz?id={quizId}&wordCount=1";
        await Page.GotoAsync(genericUrl);
        var genericSession = await Page.Locator("input[name='sessionId']").First.InputValueAsync();

        await Page.GotoAsync($"{genericUrl}&selectedWordIds={Uri.EscapeDataString(wordId)}&wordRangeStart=20&wordRangeEnd=80");
        var selectedSession = await Page.Locator("input[name='sessionId']").First.InputValueAsync();
        Assert.NotEqual(genericSession, selectedSession);
        var reset = Page.Locator("form.session-reset");
        await Expect(reset.Locator("input[name='selectedWordIds']")).ToHaveValueAsync(wordId);
        await Expect(reset.Locator("input[name='wordRangeStart']")).ToHaveValueAsync("20");
        await Expect(reset.Locator("input[name='wordRangeEnd']")).ToHaveValueAsync("80");
        await reset.GetByRole(AriaRole.Button).ClickAsync();
        await Expect(Page.Locator("input[name='sessionId']").First).Not.ToHaveValueAsync(selectedSession);
        await Expect(Page.Locator("form.session-reset input[name='selectedWordIds']")).ToHaveValueAsync(wordId);
        await Expect(Page.Locator("form.session-reset input[name='wordRangeStart']")).ToHaveValueAsync("20");
        await Expect(Page.Locator("form.session-reset input[name='wordRangeEnd']")).ToHaveValueAsync("80");

        await Page.GotoAsync(genericUrl);
        await Expect(Page.Locator("input[name='sessionId']").First).ToHaveValueAsync(genericSession);
    }

}
