using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace Glosify.BrowserTests;

public sealed class PortfolioJourneys : IAsyncLifetime
{
    private static string? BaseUrl => Environment.GetEnvironmentVariable("GLOSIFY_BROWSER_BASE_URL");
    private static int _nextTestClient;
    private string TestClientIp { get; set; } = null!;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage Page { get; set; } = null!;
    private readonly List<string> _pageErrors = [];
    private readonly List<string> _consoleErrors = [];
    private readonly List<string> _responseErrors = [];
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
        TestClientIp = $"192.0.2.{Interlocked.Increment(ref _nextTestClient)}";
        _context = await _browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = BaseUrl });
        Page = await _context.NewPageAsync();
        Page.PageError += (_, error) => _pageErrors.Add(error);
        Page.Console += (_, message) =>
        {
            if (message.Type == "error"
                && (string.IsNullOrWhiteSpace(message.Location) || IsApplicationUrl(message.Location)))
                _consoleErrors.Add(message.Text);
        };
        Page.Response += (_, response) =>
        {
            if (response.Status >= 400
                && IsApplicationUrl(response.Url)
                && !IsIgnorableResponse(response.Url))
                _responseErrors.Add($"HTTP {response.Status} {response.Url}");
            if (response.Url.Contains(".js", StringComparison.OrdinalIgnoreCase))
                _scriptResponses.Add($"{response.Status} {response.Url}");
        };
        Page.RequestFailed += (_, request) => _scriptResponses.Add($"FAILED {request.Url}: {request.Failure}");
    }

    private static bool IsIgnorableResponse(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        return uri.AbsolutePath.Contains("favicon", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.EndsWith(".map", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsApplicationUrl(string url)
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var application)
            || !Uri.TryCreate(url, UriKind.Absolute, out var candidate))
            return false;
        return string.Equals(application.Scheme, candidate.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(application.Host, candidate.Host, StringComparison.OrdinalIgnoreCase)
            && application.Port == candidate.Port;
    }

    public async Task DisposeAsync()
    {
        if (_context is not null) await _context.DisposeAsync();
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }

    private async Task AssertLanguageCatalogSupportsSearchKeyboardMobileAndNoJavaScriptSelectionAsync()
    {
        await Page.GotoAsync("/Languages?returnUrl=%2FQuizzes");
        var cards = Page.Locator("[data-language-card]");
        var expectedCountText = await Page.Locator("[data-language-picker]")
            .GetAttributeAsync("data-language-count");
        Assert.True(int.TryParse(expectedCountText, out var expectedCount));
        await Expect(cards).ToHaveCountAsync(expectedCount);

        var search = Page.GetByLabel("Find a mode or language");
        await search.FillAsync("Freestyle");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Freestyle", Exact = true })).ToBeVisibleAsync();
        await search.FillAsync("not-a-language");
        await Expect(Page.Locator("[data-language-empty]")).ToBeVisibleAsync();
        await search.FillAsync("Portuguese");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Portuguese (Brazil)", Exact = true })).ToBeVisibleAsync();
        await search.FillAsync("العربية");
        await search.PressAsync("ArrowDown");
        var arabic = Page.GetByRole(AriaRole.Button, new() { Name = "Arabic", Exact = true });
        await Expect(arabic).ToBeFocusedAsync();

        await Page.SetViewportSizeAsync(390, 844);
        var box = await arabic.BoundingBoxAsync();
        Assert.NotNull(box);
        Assert.True(box.Width <= 390);
        await arabic.PressAsync("Enter");
        await Expect(Page).ToHaveURLAsync(new Regex("/Quizzes$", RegexOptions.IgnoreCase));

        var state = await _context!.StorageStateAsync();
        await using var noJavaScriptContext = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = BaseUrl,
            JavaScriptEnabled = false,
            StorageState = state,
        });
        var noJavaScriptPage = await noJavaScriptContext.NewPageAsync();
        await noJavaScriptPage.GotoAsync("/Languages?returnUrl=%2FQuizzes");
        await noJavaScriptPage.GetByRole(AriaRole.Button, new() { Name = "Serbian (Latin)", Exact = true }).ClickAsync();
        await Expect(noJavaScriptPage).ToHaveURLAsync(new Regex("/Quizzes$", RegexOptions.IgnoreCase));
    }

    [Fact]
    [Trait("Category", "Browser")]
    public async Task RegisterLoginLogoutAndProtectedRedirect()
    {
        if (BaseUrl is null) return;

        await Page.GotoAsync("/Quizzes");
        await Expect(Page).ToHaveURLAsync(new Regex("/login", RegexOptions.IgnoreCase));

        var credentials = await RegisterAsync();
        await AssertLanguageCatalogSupportsSearchKeyboardMobileAndNoJavaScriptSelectionAsync();
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
    public async Task PreviewAndImportExternalAiJsonHierarchy()
    {
        if (BaseUrl is null) return;

        await RegisterAndSelectPolishAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Import JSON", Exact = true }).First.ClickAsync();
        const string json = """
            {
              "version": 1,
              "source_language": "English",
              "quizzes": [{
                "name": "Imported root quiz",
                "words": [{ "word": "dom", "translation": "house" }],
                "sentences": []
              }],
              "collections": [{
                "name": "Imported travel",
                "quizzes": [{
                  "name": "Imported station",
                  "words": [],
                  "sentences": [{ "text": "Gdzie jest pociąg?", "translation": "Where is the train?" }]
                }],
                "collections": [{ "name": "Imported empty child", "quizzes": [], "collections": [] }]
              }]
            }
            """;
        await Page.GetByLabel("2. Paste generated JSON").FillAsync(json);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Preview JSON" }).ClickAsync();

        var preview = Page.Locator("[data-json-import-preview]");
        await Expect(preview).ToBeVisibleAsync();
        await Expect(preview).ToContainTextAsync("Imported root quiz");
        await Expect(preview).ToContainTextAsync("Imported travel");
        await Expect(preview).ToContainTextAsync("2 quizzes");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Import everything" }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex("/Quizzes$", RegexOptions.IgnoreCase));
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Imported root quiz" })).ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("Imported travel") }).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Imported station" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Imported empty child" })).ToBeVisibleAsync();
        await AssertNoPageErrorsAsync();
    }

    [Fact]
    [Trait("Category", "Browser")]
    public async Task CreateLinkStudyAndInspectAnkiCollection()
    {
        if (BaseUrl is null) return;

        await RegisterAndSelectPolishAsync();
        await CreateQuizWithWordAsync();
        await Page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("Start Quiz", RegexOptions.IgnoreCase) }).ClickAsync();

        await Page.GetByText("Create a new compatible collection", new() { Exact = true }).ClickAsync();
        var createForm = Page.GetByRole(AriaRole.Form, new() { Name = "Create Anki collection from quiz" });
        await createForm.GetByLabel("Name", new() { Exact = true }).FillAsync("Portfolio Anki");
        await createForm.GetByRole(AriaRole.Button, new() { Name = "Create and link" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("/Anki/Collection", RegexOptions.IgnoreCase));
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Portfolio Anki", Exact = true }).First).ToBeVisibleAsync();
        await Expect(Page.Locator(".anki-list").GetByText("Portfolio Polish", new() { Exact = true })).ToBeVisibleAsync();

        // Adding the already-linked word individually is intentionally idempotent and
        // preserves the same durable card while recording both inclusion sources.
        await Page.GetByRole(AriaRole.Form, new() { NameRegex = new Regex("^Add .+ to Anki$") }).First
            .GetByRole(AriaRole.Button, new() { Name = "Add", Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Link, new() { Name = "Start session" }).ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Show answer" }).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "dom", Exact = true })).ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("good", RegexOptions.IgnoreCase) }).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "You’re done for now" })).ToBeVisibleAsync();

        await Page.GetByRole(AriaRole.Link, new() { Name = "Back to collections" }).ClickAsync();
        await Page.GetByRole(AriaRole.Link, new() { Name = "Portfolio Anki" }).ClickAsync();
        await Expect(Page.GetByText("Studied today").Locator("..").Locator("strong")).ToHaveTextAsync("1");
        await Expect(Page.GetByText("30-day retention").Locator("..").Locator("strong")).ToHaveTextAsync("100%");
        await AssertNoPageErrorsAsync();
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
        var chatsPane = Page.Locator("[data-assistant-pane='chats']");

        async Task OpenChatsAsync()
        {
            await Page.Locator("[data-assistant-tab='chats']").ClickAsync();
            await Expect(chatsPane).ToBeVisibleAsync();
        }

        // Opening initializes/selects the first chat and finishes by activating the
        // chat pane. Wait for that state before choosing Chats, otherwise a slow SQL
        // test host can switch the pane back while this click is in flight.
        await Expect(Page.Locator("[data-assistant-pane='chat']")).ToBeVisibleAsync();
        await AssertNoPageErrorsAsync();
        await OpenChatsAsync();
        await Expect(Page.Locator("[data-assistant-new-chat]")).ToBeVisibleAsync();
        await Expect(Page.Locator("[data-assistant-chat-item]")).ToHaveCountAsync(1);

        await Page.Locator("[data-assistant-new-chat]").ClickAsync();
        // Creating a chat renders the list once when the POST completes and again after
        // selection/history loading. Wait for the operation's final pane transition so
        // the Chats click below cannot race that second render and replace the row while
        // Playwright is hovering it.
        await Expect(Page.Locator("[data-assistant-pane='chat']")).ToBeVisibleAsync();
        await OpenChatsAsync();
        await Expect(Page.Locator("[data-assistant-chat-item]")).ToHaveCountAsync(2);

        async void RenameDialog(object? _, IDialog dialog) => await dialog.AcceptAsync("Employer demo chat");
        Page.Dialog += RenameDialog;
        await Page.Locator("[data-assistant-chat-item]").First.HoverAsync();
        await Page.Locator("[data-assistant-chat-item]").First
            .Locator("button[aria-label='Rename chat']")
            .ClickAsync();
        Page.Dialog -= RenameDialog;
        await Expect(Page.Locator("[data-assistant-chat-item]").First).ToContainTextAsync("Employer demo chat");

        // Renaming replaces the list rows asynchronously. Reassert the pane before
        // interacting with the replacement row so a delayed selection transition
        // cannot leave Playwright targeting a row inside the hidden Chats pane.
        await OpenChatsAsync();
        async void DeleteDialog(object? _, IDialog dialog) => await dialog.AcceptAsync();
        Page.Dialog += DeleteDialog;
        await Page.Locator("[data-assistant-chat-item]").First.HoverAsync();
        await Page.Locator("[data-assistant-chat-item]").First
            .Locator("button[aria-label='Delete chat']")
            .ClickAsync();
        Page.Dialog -= DeleteDialog;
        await Expect(Page.Locator("[data-assistant-chat-item]")).ToHaveCountAsync(1);
        await Expect(Page.Locator("[data-assistant-pane='chat']")).ToBeVisibleAsync();
        await OpenChatsAsync();
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
        await Expect(modelSelector.Locator("option")).ToHaveCountAsync(4);
        await Expect(modelSelector.Locator("option[value='']"))
            .ToContainTextAsync("Auto · GPT-5.6 Luna · OpenAI · Balanced · 1× credits");
        await Expect(modelSelector.Locator("option[value='gpt-5.6-luna']"))
            .ToContainTextAsync("Balanced · 1× credits");
        await Expect(modelSelector.Locator("option[value='grok-4.3']"))
            .ToContainTextAsync("Thoughtful · 0.6× credits");
        await Expect(modelSelector.Locator("option[value='DeepSeek-V4-Flash']"))
            .ToContainTextAsync("Economy · 0.3× credits");
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
        await Expect(Page.Locator("[data-assistant-status]")).ToContainTextAsync("Could not apply changes.");
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

    [Fact]
    [Trait("Category", "Browser")]
    public async Task SwedishDisplayLanguagePersistsAcrossNavigationReloadMobileAndSignIn()
    {
        if (BaseUrl is null) return;

        await Page.GotoAsync("/");
        await Page.GetByLabel("Display language").SelectOptionAsync("sv-SE");
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("lang", "sv-SE");
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Gör nya ord till riktiga samtal." })).ToBeVisibleAsync();
        await Page.ReloadAsync();
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("lang", "sv-SE");

        await Page.SetViewportSizeAsync(390, 844);
        var mobileSelector = Page.GetByLabel("Visningsspråk");
        await Expect(mobileSelector).ToBeVisibleAsync();
        Assert.NotNull(await mobileSelector.BoundingBoxAsync());

        var email = $"sv-e2e-{Guid.NewGuid():N}@example.test";
        const string password = "Portfolio!123";
        await RouteRegistrationAsTestClientAsync();
        await Page.GotoAsync("/Account/Register");
        await Page.GetByLabel("E-postadress").FillAsync(email);
        await Page.GetByLabel("Lösenord", new() { Exact = true }).FillAsync(password);
        await Page.GetByLabel("Bekräfta lösenord").FillAsync(password);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Skapa konto" }).ClickAsync();
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("lang", "sv-SE");

        await using var otherDevice = await _browser!.NewContextAsync(new BrowserNewContextOptions { BaseURL = BaseUrl });
        var signedInPage = await otherDevice.NewPageAsync();
        await signedInPage.GotoAsync("/login");
        await signedInPage.GetByLabel("Email Address").FillAsync(email);
        await signedInPage.GetByLabel("Password", new() { Exact = true }).FillAsync(password);
        await signedInPage.GetByRole(AriaRole.Button, new() { Name = "Log In" }).ClickAsync();
        await Expect(signedInPage.Locator("html")).ToHaveAttributeAsync("lang", "sv-SE");

        await signedInPage.GotoAsync("/Languages");
        await signedInPage.Locator("button[name='language'][value='pl']").ClickAsync();
        foreach (var route in new[] { "/", "/Quizzes", "/Books", "/Speaking", "/Classroom", "/Transcripts" })
        {
            await signedInPage.GotoAsync(route);
            await Expect(signedInPage.Locator("html")).ToHaveAttributeAsync("lang", "sv-SE");
        }
    }

    [Fact]
    [Trait("Category", "Browser")]
    public async Task EveryDisplayLanguageSwitchesPersistsAndKeepsPublicRoutesAccessible()
    {
        if (BaseUrl is null) return;

        var cultures = new[]
        {
            "en-GB", "sv-SE", "es-419", "pt-BR", "fr-FR", "ja-JP",
            "zh-Hans", "uk-UA", "tr-TR", "id-ID", "vi-VN", "ar",
        };
        await Page.GotoAsync("/");
        foreach (var culture in cultures)
        {
            await Page.Locator("select[name='culture']").SelectOptionAsync(culture);
            await Expect(Page.Locator("html")).ToHaveAttributeAsync("lang", culture);
            await Expect(Page.Locator("html")).ToHaveAttributeAsync("dir", culture == "ar" ? "rtl" : "ltr");
            await Page.ReloadAsync();
            await Expect(Page.Locator("html")).ToHaveAttributeAsync("lang", culture);
            await Expect(Page.Locator("html")).ToHaveAttributeAsync("dir", culture == "ar" ? "rtl" : "ltr");
        }

        foreach (var culture in new[] { "es-419", "ja-JP", "uk-UA", "ar" })
        {
            foreach (var suffix in new[] { "", "/privacy", "/terms", "/support" })
            {
                await Page.GotoAsync($"/{culture}{suffix}");
                await Expect(Page.Locator("html")).ToHaveAttributeAsync("lang", culture);
                await Expect(Page.Locator("link[rel='canonical']")).ToHaveCountAsync(1);
                await Expect(Page.Locator("link[rel='alternate'][hreflang='x-default']")).ToHaveCountAsync(1);
            }
        }

        await Page.SetViewportSizeAsync(390, 844);
        await Page.GotoAsync("/ar");
        var selector = Page.Locator("select[name='culture']");
        await Expect(selector).ToBeVisibleAsync();
        Assert.NotNull(await selector.BoundingBoxAsync());
        Assert.NotEmpty(await Page.ScreenshotAsync(new PageScreenshotOptions { FullPage = true }));
        await AssertNoPageErrorsAsync();
    }

    private async Task<(string Email, string Password)> RegisterAsync()
    {
        var email = $"e2e-{Guid.NewGuid():N}@example.test";
        const string password = "Portfolio!123";
        await RouteRegistrationAsTestClientAsync();
        await Page.GotoAsync("/Account/Register");
        await Page.GetByLabel("Email Address").FillAsync(email);
        await Page.GetByLabel("Password", new() { Exact = true }).FillAsync(password);
        await Page.GetByLabel("Confirm Password").FillAsync(password);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Create Account" }).ClickAsync();
        await Expect(Page).Not.ToHaveURLAsync(new Regex("/Account/Register", RegexOptions.IgnoreCase));
        return (email, password);
    }

    private Task RouteRegistrationAsTestClientAsync() =>
        Page.RouteAsync("**/Account/Register*", route =>
        {
            var headers = new Dictionary<string, string>(route.Request.Headers, StringComparer.OrdinalIgnoreCase)
            {
                ["X-Forwarded-For"] = TestClientIp,
            };
            return route.FallbackAsync(new RouteFallbackOptions { Headers = headers });
        });

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
            _pageErrors.Count == 0 && _consoleErrors.Count == 0 && _responseErrors.Count == 0,
            $"Browser errors:{Environment.NewLine}{string.Join(Environment.NewLine, _pageErrors.Concat(_consoleErrors).Concat(_responseErrors))}");
    }
}
