using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace Glosify.BrowserTests;

public sealed class PortfolioJourneys : IAsyncLifetime
{
    private static string? BaseUrl => Environment.GetEnvironmentVariable("GLOSIFY_BROWSER_BASE_URL");
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage Page { get; set; } = null!;
    private readonly List<string> _pageErrors = [];
    private readonly List<string> _consoleErrors = [];
    private readonly List<string> _scriptResponses = [];

    public async Task InitializeAsync()
    {
        if (BaseUrl is null) return;

        _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            ExecutablePath = Environment.GetEnvironmentVariable("GLOSIFY_BROWSER_EXECUTABLE_PATH"),
            Headless = true,
        });
        _context = await _browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = BaseUrl });
        Page = await _context.NewPageAsync();
        Page.PageError += (_, error) => _pageErrors.Add(error);
        Page.Console += (_, message) =>
        {
            if (message.Type == "error") _consoleErrors.Add(message.Text);
        };
        Page.Response += (_, response) =>
        {
            if (response.Url.Contains(".js", StringComparison.OrdinalIgnoreCase))
                _scriptResponses.Add($"{response.Status} {response.Url}");
        };
        Page.RequestFailed += (_, request) => _scriptResponses.Add($"FAILED {request.Url}: {request.Failure}");
    }

    public async Task DisposeAsync()
    {
        if (_context is not null) await _context.DisposeAsync();
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }

    [Fact]
    [Trait("Category", "Browser")]
    public async Task RegisterLoginLogoutAndProtectedRedirect()
    {
        if (BaseUrl is null) return;

        await Page.GotoAsync("/Quizzes");
        await Expect(Page).ToHaveURLAsync(new Regex("/login", RegexOptions.IgnoreCase));

        var credentials = await RegisterAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Log out" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("/$"));

        await Page.GotoAsync("/login");
        await Page.GetByLabel("Email Address").FillAsync(credentials.Email);
        await Page.GetByLabel("Password").FillAsync(credentials.Password);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Log In" }).ClickAsync();
        await Expect(Page).Not.ToHaveURLAsync(new Regex("/login", RegexOptions.IgnoreCase));
    }

    [Fact]
    [Trait("Category", "Browser")]
    public async Task CreateQuizAddWordAndStartPractice()
    {
        if (BaseUrl is null) return;

        await RegisterAndSelectPolishAsync();
        await CreateQuizWithWordAsync();

        await Page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("Start Quiz", RegexOptions.IgnoreCase) }).ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Start Quiz", RegexOptions.IgnoreCase) }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex("/FlashcardQuiz", RegexOptions.IgnoreCase));
        await Expect(Page.Locator("[data-flashcard-session]")).ToBeVisibleAsync();
    }

    [Fact]
    [Trait("Category", "Browser")]
    public async Task CreateSaveAndPlayCustomQuiz()
    {
        if (BaseUrl is null) return;

        await RegisterAndSelectPolishAsync();
        await CreateQuizWithWordAsync();
        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Build custom quiz", RegexOptions.IgnoreCase) }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("/CustomQuizzes/.+/Edit", RegexOptions.IgnoreCase));
        await Expect(Page.Locator("script[src*='custom-quiz-editor.js']")).ToHaveCountAsync(1);
        var runtimeText = await Page.Locator("[data-custom-runtime]").InnerTextAsync();
        Assert.True(
            !runtimeText.Contains("Loading editor controls", StringComparison.OrdinalIgnoreCase),
            $"Custom editor did not initialize. Script responses:{Environment.NewLine}{string.Join(Environment.NewLine, _scriptResponses)}");

        await Page.GetByRole(AriaRole.Button, new() { Name = "Use layout" }).First.ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Add Text input", Exact = true }).ClickAsync();
        await Page.GetByLabel("Custom quiz name").FillAsync("Portfolio custom quiz");
        await AssertNoPageErrorsAsync();
        await Page.Locator("[data-custom-save]").ClickAsync();
        await Page.WaitForTimeoutAsync(250);
        await AssertNoPageErrorsAsync();
        await Expect(Page.Locator("[data-custom-message]")).ToContainTextAsync("Saved");

        var match = Regex.Match(Page.Url, @"/CustomQuizzes/(?<id>[0-9a-f-]+)/Edit", RegexOptions.IgnoreCase);
        Assert.True(match.Success, $"Could not read the custom quiz id from {Page.Url}.");
        await Page.GotoAsync($"/CustomQuizzes/{match.Groups["id"].Value}/Play");
        await Expect(Page.Locator("[data-custom-player]")).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Portfolio custom quiz" })).ToBeVisibleAsync();
    }

    [Fact]
    [Trait("Category", "Browser")]
    public async Task CreateRenameSwitchAndDeleteAssistantChatsWithoutAi()
    {
        if (BaseUrl is null) return;

        await RegisterAndSelectPolishAsync();
        await Page.GotoAsync("/Quizzes");
        await Page.Locator("[data-assistant-toggle]").ClickAsync();
        // Opening initializes/selects the first chat and finishes by activating the
        // chat pane. Wait for that state before choosing Chats, otherwise a slow SQL
        // test host can switch the pane back while this click is in flight.
        await Expect(Page.Locator("[data-assistant-pane='chat']")).ToBeVisibleAsync();
        await AssertNoPageErrorsAsync();
        await Page.Locator("[data-assistant-tab='chats']").ClickAsync();
        await Expect(Page.Locator("[data-assistant-new-chat]")).ToBeVisibleAsync();
        await Expect(Page.Locator("[data-assistant-chat-item]")).ToHaveCountAsync(1);

        await Page.Locator("[data-assistant-new-chat]").ClickAsync();
        // Creating a chat renders the list once when the POST completes and again after
        // selection/history loading. Wait for the operation's final pane transition so
        // the Chats click below cannot race that second render and replace the row while
        // Playwright is hovering it.
        await Expect(Page.Locator("[data-assistant-pane='chat']")).ToBeVisibleAsync();
        await Page.Locator("[data-assistant-tab='chats']").ClickAsync();
        await Expect(Page.Locator("[data-assistant-chat-item]")).ToHaveCountAsync(2);

        async void RenameDialog(object? _, IDialog dialog) => await dialog.AcceptAsync("Employer demo chat");
        Page.Dialog += RenameDialog;
        await Page.Locator("[data-assistant-chat-item]").First.HoverAsync();
        await Page.Locator("[data-assistant-chat-item]").First
            .Locator("button[aria-label='Rename chat']")
            .ClickAsync();
        Page.Dialog -= RenameDialog;
        await Expect(Page.Locator("[data-assistant-chat-item]").First).ToContainTextAsync("Employer demo chat");

        async void DeleteDialog(object? _, IDialog dialog) => await dialog.AcceptAsync();
        Page.Dialog += DeleteDialog;
        await Page.Locator("[data-assistant-chat-item]").First.HoverAsync();
        await Page.Locator("[data-assistant-chat-item]").First
            .Locator("button[aria-label='Delete chat']")
            .ClickAsync();
        Page.Dialog -= DeleteDialog;
        await Expect(Page.Locator("[data-assistant-chat-item]")).ToHaveCountAsync(1);
        await Page.Locator("[data-assistant-tab='chats']").ClickAsync();
        await Page.Locator(".assistant-chat-main").ClickAsync();
        await Expect(Page.Locator("[data-assistant-pane='chat']")).ToBeVisibleAsync();
    }

    [Fact]
    [Trait("Category", "Browser")]
    public async Task AssistantApplySelectsReturnedQuizWithoutCallingBearerQuizApi()
    {
        if (BaseUrl is null) return;

        var createdQuizId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var apiQuizRequests = 0;
        var applyAttempts = 0;
        var failContextPatch = false;
        string? contextPatch = null;
        Page.Request += (_, request) =>
        {
            if (new Uri(request.Url).AbsolutePath.Equals("/api/quizzes", StringComparison.OrdinalIgnoreCase))
                apiQuizRequests++;
        };

        await Page.RouteAsync("**/Assistant/Chats/*/Send", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = JsonSerializer.Serialize(new
            {
                threadId = Guid.NewGuid(),
                assistantMessageId,
                assistantText = "I prepared a travel quiz.",
                toolEvents = Array.Empty<object>(),
                // Both content types in one standard-quiz proposal, which is the shape the
                // review card has to render since starter sentences became part of creation.
                pendingChanges = new[]
                {
                    new
                    {
                        kind = "create_quiz",
                        summary = "Create quiz \"Travel Polish\" with 5 words and 5 sentences (English -> Polish)",
                    },
                },
                status = "active",
            }),
        }));
        await Page.RouteAsync("**/Assistant/Apply/*", route =>
        {
            applyAttempts++;
            return route.FulfillAsync(new RouteFulfillOptions
            {
                Status = applyAttempts == 1 ? 409 : 200,
                ContentType = applyAttempts == 1 ? "application/problem+json" : "application/json",
                Body = applyAttempts == 1
                    ? JsonSerializer.Serialize(new
                    {
                        status = 409,
                        code = "collection_name_conflict",
                        detail = "The target collection changed. Retry the proposal.",
                    })
                    : JsonSerializer.Serialize(new
                    {
                        applied = 1,
                        createdQuizId,
                        createdQuiz = new
                        {
                            id = createdQuizId,
                            name = "Travel Polish",
                            sourceLanguage = "English",
                            targetLanguage = "Polish",
                        },
                        createdCollectionId = (Guid?)null,
                        createdCustomQuizId = (Guid?)null,
                        createdCustomQuizElements = 0,
                    }),
            });
        });
        await Page.RouteAsync("**/Assistant/Chats/*", async route =>
        {
            if (!route.Request.Method.Equals("PATCH", StringComparison.OrdinalIgnoreCase))
            {
                await route.ContinueAsync();
                return;
            }

            contextPatch = route.Request.PostData;
            if (failContextPatch)
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 503,
                    ContentType = "application/problem+json",
                    Body = JsonSerializer.Serialize(new
                    {
                        status = 503,
                        code = "temporarily_unavailable",
                        detail = "Context persistence is temporarily unavailable.",
                    }),
                });
                return;
            }
            var threadId = new Uri(route.Request.Url).Segments.Last().Trim('/');
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = JsonSerializer.Serialize(new
                {
                    id = threadId,
                    title = "New chat",
                    updatedAt = DateTimeOffset.UtcNow,
                    contextQuizId = createdQuizId,
                    contextQuizName = "Travel Polish",
                    preview = "",
                }),
            });
        });
        await Page.RouteAsync($"**/Quizzes/Details/{createdQuizId}", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "text/html",
            Body = "<html><body><h1>Travel Polish</h1></body></html>",
        }));

        await RegisterAndSelectPolishAsync();
        await Page.GotoAsync("/Quizzes");
        await Page.Locator("[data-assistant-toggle]").ClickAsync();
        var modelSelector = Page.Locator("[data-assistant-model-select]");
        await Expect(modelSelector.Locator("option")).ToHaveCountAsync(5);
        await Expect(modelSelector.Locator("option[value='']"))
            .ToContainTextAsync("Auto · GPT-5.6 Luna · OpenAI · Balanced · Cost ≈1× · 1× credits");
        await Expect(modelSelector.Locator("option[value='gpt-5.6-luna']"))
            .ToContainTextAsync("Cost ≈1× · 1× credits");
        await Expect(modelSelector.Locator("option[value='grok-4.3']"))
            .ToContainTextAsync("Cost ≈1× · 1× credits");
        await Expect(modelSelector.Locator("option[value='gpt-5.6-sol']"))
            .ToContainTextAsync("Most powerful · Cost ≈5× · 5× credits");
        await Expect(modelSelector.Locator("option[value='DeepSeek-V4-Flash']"))
            .ToContainTextAsync("Economy · Cost ≈0.25× · 0.25× credits");
        await Page.Locator("[data-assistant-textarea]").FillAsync("Create a travel quiz");
        await Page.Locator("[data-assistant-submit]").ClickAsync();
        await Expect(Page.Locator("[data-assistant-pending-card]")).ToBeVisibleAsync();

        var pendingCard = Page.Locator("[data-assistant-pending-card]");
        // The card has to name a standard quiz and account for both content types, so an
        // unwanted custom quiz or a silently dropped set of sentences is visible before Apply.
        await Expect(pendingCard).ToContainTextAsync("5 words and 5 sentences");
        await Expect(pendingCard).Not.ToContainTextAsync("custom quiz");
        var applyButton = pendingCard.GetByRole(AriaRole.Button, new() { Name = "Apply", Exact = true });
        var rejectButton = pendingCard.GetByRole(AriaRole.Button, new() { Name = "Reject", Exact = true });
        await applyButton.ClickAsync();
        await Expect(Page.Locator("[data-assistant-status]")).ToContainTextAsync("target collection changed");
        await Expect(applyButton).ToBeEnabledAsync();
        await Expect(rejectButton).ToBeEnabledAsync();

        await applyButton.ClickAsync();

        await Expect(Page.Locator("[data-assistant-quiz-selector]")).ToHaveValueAsync(createdQuizId.ToString());
        await Expect(Page.Locator($"[data-assistant-quiz-selector] option[value='{createdQuizId}']"))
            .ToContainTextAsync("Travel Polish (English -> Polish)");
        Assert.NotNull(contextPatch);
        using (var patchJson = JsonDocument.Parse(contextPatch))
        {
            Assert.Equal(createdQuizId, patchJson.RootElement.GetProperty("contextQuizId").GetGuid());
            Assert.True(patchJson.RootElement.GetProperty("updateContext").GetBoolean());
        }
        Assert.Equal(0, apiQuizRequests);

        failContextPatch = true;
        await Page.Locator("[data-assistant-quiz-selector]").SelectOptionAsync("");
        await Expect(Page.Locator("[data-assistant-status]")).ToContainTextAsync("Could not save chat context");

        await Page.GetByRole(AriaRole.Link, new() { Name = "Open quiz" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex($"/Quizzes/Details/{createdQuizId}$", RegexOptions.IgnoreCase));
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Travel Polish" })).ToBeVisibleAsync();
    }

    private async Task<(string Email, string Password)> RegisterAsync()
    {
        var email = $"e2e-{Guid.NewGuid():N}@example.test";
        const string password = "Portfolio!123";
        await Page.GotoAsync("/Account/Register");
        await Page.GetByLabel("Email Address").FillAsync(email);
        await Page.GetByLabel("Password", new() { Exact = true }).FillAsync(password);
        await Page.GetByLabel("Confirm Password").FillAsync(password);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Create Account" }).ClickAsync();
        await Expect(Page).Not.ToHaveURLAsync(new Regex("/Account/Register", RegexOptions.IgnoreCase));
        return (email, password);
    }

    private async Task RegisterAndSelectPolishAsync()
    {
        await RegisterAsync();
        await Page.GotoAsync("/Languages");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Polish", Exact = true }).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex("Polish", RegexOptions.IgnoreCase) }).First).ToBeVisibleAsync();
        await Page.GotoAsync("/Quizzes");
    }

    private async Task CreateQuizWithWordAsync()
    {
        await Page.GotoAsync("/Quizzes");
        await Page.GetByRole(AriaRole.Button, new() { Name = "New Collection" }).ClickAsync();
        await Page.GetByLabel("Collection Name").FillAsync("Portfolio collection");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Create Collection" }).Last.ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Portfolio collection" })).ToBeVisibleAsync();

        await Page.GetByRole(AriaRole.Button, new() { Name = "New Quiz" }).ClickAsync();
        await Page.GetByLabel("Quiz Name").FillAsync("Portfolio Polish");
        await Page.GetByLabel("Source Language").SelectOptionAsync(new SelectOptionValue { Label = "English" });
        await Page.GetByRole(AriaRole.Button, new() { Name = "Create Quiz" }).Last.ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Portfolio Polish" })).ToBeVisibleAsync();

        await Page.GetByLabel("Word").FillAsync("dom");
        await Page.GetByLabel("Translation").FillAsync("house");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Add word" }).ClickAsync();
        await Expect(Page.Locator(".word-card")).ToContainTextAsync("dom");
    }

    private async Task AssertNoPageErrorsAsync()
    {
        await Page.WaitForTimeoutAsync(100);
        Assert.True(
            _pageErrors.Count == 0 && _consoleErrors.Count == 0,
            $"Browser errors:{Environment.NewLine}{string.Join(Environment.NewLine, _pageErrors.Concat(_consoleErrors))}");
    }
}
