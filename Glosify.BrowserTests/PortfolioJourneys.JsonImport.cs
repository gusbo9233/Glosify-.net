using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace Glosify.BrowserTests;

public sealed partial class PortfolioJourneys
{
    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task DelayedJsonPreviewDoesNotOverwriteNewerEdits()
    {
        var input = await OpenJsonImportAsync();
        const string submittedJson = """
            { "version": 1, "source_language": "English", "quizzes": [], "collections": [] }
            """;
        const string newerJson = """
            { "version": 1, "source_language": "English", "quizzes": [], "collections": [{ "name": "Keep this edit", "quizzes": [], "collections": [] }] }
            """;
        var requestSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await Page.RouteAsync("**/Quiz/PreviewJsonImport", async route =>
        {
            requestSeen.TrySetResult();
            await releaseResponse.Task;
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = PreviewResponse(submittedJson),
            });
        });

        await input.FillAsync(submittedJson);
        var previewClick = Page.Locator("[data-json-import-preview-button]").ClickAsync();
        try
        {
            await requestSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await input.FillAsync(newerJson);
        }
        finally
        {
            releaseResponse.TrySetResult();
        }
        await previewClick;

        await Expect(input).ToHaveValueAsync(newerJson);
        await Expect(Page.Locator("[data-json-import-apply]")).ToBeHiddenAsync();
        await Expect(Page.Locator("[data-json-import-status]"))
            .ToContainTextAsync("older response was discarded");
    }

    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task DelayedAiJsonRepairDoesNotOverwriteNewerEditsAndWarnsAboutCredits()
    {
        var input = await OpenJsonImportAsync();
        const string invalidJson = "{ invalid";
        const string newerJson = """
            { "version": 1, "source_language": "English", "quizzes": [], "collections": [{ "name": "Keep this AI edit", "quizzes": [], "collections": [] }] }
            """;

        ExpectHttpFailure("POST", "/Quiz/PreviewJsonImport", 400);
        await Page.RouteAsync("**/Quiz/PreviewJsonImport", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 400,
            ContentType = "application/problem+json",
            Body = JsonSerializer.Serialize(new
            {
                title = "Invalid quiz import",
                status = 400,
                errors = new Dictionary<string, string[]> { ["$.json"] = ["Expected valid JSON."] },
            }),
        }));
        await input.FillAsync(invalidJson);
        await Page.Locator("[data-json-import-preview-button]").ClickAsync();
        var repairButton = Page.Locator("[data-json-import-ai-repair]");
        await Expect(repairButton).ToBeVisibleAsync();
        await Page.UnrouteAsync("**/Quiz/PreviewJsonImport");

        var requestSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Page.RouteAsync("**/Quiz/RepairJsonImportWithAi", async route =>
        {
            requestSeen.TrySetResult();
            await releaseResponse.Task;
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = PreviewResponse(invalidJson),
            });
        });

        var repairClick = repairButton.ClickAsync();
        try
        {
            await requestSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await input.FillAsync(newerJson);
        }
        finally
        {
            releaseResponse.TrySetResult();
        }
        await repairClick;

        await Expect(input).ToHaveValueAsync(newerJson);
        await Expect(Page.Locator("[data-json-import-apply]")).ToBeHiddenAsync();
        await Expect(Page.Locator("[data-json-import-status]"))
            .ToContainTextAsync("may still have used credits");
    }

    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task DelayedJsonApplyLocksCommittedInputAndOnlyReenablesAfterFailure()
    {
        var input = await OpenJsonImportAsync();
        const string json = """
            {
              "version": 1,
              "source_language": "English",
              "quizzes": [{
                "name": "Apply lock",
                "words": [{ "word": "dom", "translation": "house" }],
                "sentences": []
              }],
              "collections": []
            }
            """;
        await input.FillAsync(json);
        await Page.Locator("[data-json-import-preview-button]").ClickAsync();
        var applyButton = Page.Locator("[data-json-import-apply]");
        await Expect(applyButton).ToBeVisibleAsync();

        var firstRequestSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRequestSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempt = 0;
        string? firstRequestBody = null;
        string? secondRequestBody = null;

        ExpectHttpFailure("POST", "/Quiz/ApplyJsonImport", 409);
        await Page.RouteAsync("**/Quiz/ApplyJsonImport", async route =>
        {
            switch (Interlocked.Increment(ref attempt))
            {
                case 1:
                    firstRequestBody = route.Request.PostData;
                    firstRequestSeen.TrySetResult();
                    await releaseFirstResponse.Task;
                    await route.FulfillAsync(new RouteFulfillOptions
                    {
                        Status = 409,
                        ContentType = "application/problem+json",
                        Body = JsonSerializer.Serialize(new
                        {
                            title = "Import could not be committed",
                            status = 409,
                            detail = "The delayed test rejected this attempt.",
                        }),
                    });
                    break;
                case 2:
                    secondRequestBody = route.Request.PostData;
                    secondRequestSeen.TrySetResult();
                    await releaseSecondResponse.Task;
                    await route.FulfillAsync(new RouteFulfillOptions
                    {
                        Status = 200,
                        ContentType = "application/json",
                        Body = JsonSerializer.Serialize(new
                        {
                            collectionCount = 0,
                            quizCount = 1,
                            wordCount = 1,
                            sentenceCount = 0,
                            redirectUrl = "/Quizzes",
                        }),
                    });
                    break;
                default:
                    await route.AbortAsync();
                    break;
            }
        });

        var firstApplyClick = applyButton.ClickAsync();
        try
        {
            await firstRequestSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Expect(input).ToBeDisabledAsync();
            Assert.Contains("Apply lock", firstRequestBody, StringComparison.Ordinal);
        }
        finally
        {
            releaseFirstResponse.TrySetResult();
        }
        await firstApplyClick;
        await Expect(input).ToBeEnabledAsync();

        await Page.Locator("[data-json-import-preview-button]").ClickAsync();
        await Expect(applyButton).ToBeVisibleAsync();
        var committedJson = await input.InputValueAsync();
        var secondApplyClick = applyButton.ClickAsync();
        try
        {
            await secondRequestSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Expect(input).ToBeDisabledAsync();
            await Expect(input).ToHaveValueAsync(committedJson);
            Assert.Contains("Apply lock", secondRequestBody, StringComparison.Ordinal);
        }
        finally
        {
            releaseSecondResponse.TrySetResult();
        }
        await secondApplyClick;

        await Expect(Page.Locator("[data-json-import-status]")).ToContainTextAsync("Import complete");
        await Expect(input).ToBeDisabledAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("/Quizzes$", RegexOptions.IgnoreCase));
    }

    private async Task<ILocator> OpenJsonImportAsync()
    {
        await RegisterAndSelectPolishAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Import JSON", Exact = true }).First.ClickAsync();
        var input = Page.GetByLabel("2. Paste generated JSON");
        await Expect(input).ToBeVisibleAsync();
        return input;
    }

    private static string PreviewResponse(string canonicalJson) => JsonSerializer.Serialize(new
    {
        canonicalJson,
        wasAutoRepaired = false,
        targetLanguage = "Polish",
        parentCollectionId = (Guid?)null,
        totals = new
        {
            collectionCount = 0,
            quizCount = 0,
            wordCount = 0,
            sentenceCount = 0,
        },
        quizzes = Array.Empty<object>(),
        collections = Array.Empty<object>(),
        warnings = Array.Empty<string>(),
    });
}
