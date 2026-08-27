using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace Glosify.BrowserTests;

public sealed partial class PortfolioJourneys : IAsyncLifetime
{
    private static int _nextTestClient;
    private readonly ConcurrentQueue<string> _pageErrors = [];
    private readonly ConcurrentQueue<string> _consoleErrors = [];
    private readonly ConcurrentQueue<string> _responseErrors = [];
    private readonly ConcurrentQueue<ObservedFailedRequest> _requestErrors = [];
    private readonly ConcurrentQueue<string> _scriptResponses = [];
    private readonly ConcurrentDictionary<IRequest, ObservedRequest> _inflightRequests = [];
    private readonly Lock _observedPagesLock = new();
    private readonly HashSet<IPage> _observedPages = [];
    private readonly Lock _expectedFailuresLock = new();
    private readonly List<BrowserFailureExpectation> _expectedFailures = [];
    private string TestClientIp { get; set; } = null!;
    private BrowserTestSettings _settings = null!;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private string? _tracePath;
    private IPage Page { get; set; } = null!;

    private string BaseUrl => _settings.BaseUri.AbsoluteUri;

    public async Task InitializeAsync()
    {
        _settings = BrowserTestConfiguration.Load();
        await ValidateHandshakeAsync(_settings);

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            ExecutablePath = Environment.GetEnvironmentVariable("GLOSIFY_BROWSER_EXECUTABLE_PATH"),
            Headless = true,
        });
        TestClientIp = $"192.0.2.{Interlocked.Increment(ref _nextTestClient)}";
        _context = await NewObservedContextAsync(new BrowserNewContextOptions { BaseURL = BaseUrl });
        if (!string.IsNullOrWhiteSpace(_settings.TraceDirectory))
        {
            var traceDirectory = Path.GetFullPath(_settings.TraceDirectory);
            Directory.CreateDirectory(traceDirectory);
            _tracePath = Path.Combine(
                traceDirectory,
                $"portfolio-journey-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.zip");
            await _context.Tracing.StartAsync(new TracingStartOptions
            {
                Screenshots = true,
                Snapshots = true,
                Sources = true,
            });
        }

        Page = await _context.NewPageAsync();
    }

    private static async Task ValidateHandshakeAsync(BrowserTestSettings settings)
    {
        using var client = new HttpClient { BaseAddress = settings.BaseUri, Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/_test/browser-handshake");
        request.Headers.TryAddWithoutValidation(BrowserTestConfiguration.RunTokenHeader, settings.RunToken);
        using var response = await client.SendAsync(request);
        if (response.StatusCode != HttpStatusCode.NoContent)
        {
            throw new InvalidOperationException(
                $"Browser test handshake failed with HTTP {(int)response.StatusCode}. "
                + "Confirm that the target is the published Glosify app in BrowserTesting and both run tokens match.");
        }
    }

    private async Task<IBrowserContext> NewObservedContextAsync(BrowserNewContextOptions options)
    {
        var context = await _browser!.NewContextAsync(options);
        // Web fonts are an external presentation dependency, not part of the application
        // contract exercised by these journeys. Stub their stylesheets so the observer can
        // treat every other failed request as a test failure without depending on the public
        // Google Fonts service or a runner's outbound-network policy.
        await context.RouteAsync("https://fonts.googleapis.com/**", route =>
            route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "text/css",
                Body = string.Empty,
            }));
        context.Page += (_, page) => ObservePage(page);
        return context;
    }

    private void ObservePage(IPage page)
    {
        lock (_observedPagesLock)
        {
            if (!_observedPages.Add(page))
                return;
        }

        page.PageError += (_, error) => _pageErrors.Enqueue($"PAGE {page.Url}: {error}");
        page.Console += (_, message) =>
        {
            if (message.Type == "error"
                // Response and RequestFailed provide the exact method, URL, and status for
                // these generic browser messages, including exact expected-failure matching.
                && !IsBrowserNetworkConsoleError(message.Text))
            {
                _consoleErrors.Enqueue($"CONSOLE {page.Url}: {message.Text}");
            }
        };
        page.Request += (_, request) => ObserveRequest(request);
        page.Response += (_, response) => RecordResponse(response);
        page.RequestFinished += (_, request) => CompleteRequest(request);
        page.RequestFailed += (_, request) =>
        {
            RecordFailedRequest(request);
            CompleteRequest(request);
        };
    }

    private void RecordResponse(IResponse response)
    {
        if (response.Url.Contains(".js", StringComparison.OrdinalIgnoreCase))
            _scriptResponses.Enqueue($"{response.Status} {response.Url}");

        if (response.Status < 400)
            return;

        if (!ConsumeExpectedFailure(BrowserFailureKind.HttpResponse, response.Request, response.Status))
        {
            _responseErrors.Enqueue(
                $"HTTP {response.Status} {response.Request.Method} {response.Url}");
        }
    }

    private void RecordFailedRequest(IRequest request)
    {
        _scriptResponses.Enqueue($"FAILED {request.Method} {request.Url}: {request.Failure}");
        if (!ConsumeExpectedFailure(BrowserFailureKind.RequestFailed, request, status: null))
        {
            var observed = _inflightRequests.TryGetValue(request, out var inflight)
                ? inflight
                : CreateObservedRequest(request);
            _requestErrors.Enqueue(new ObservedFailedRequest(
                request.Frame.Page,
                observed.InitiatorUrl,
                request.ResourceType,
                $"FAILED {request.Method} {request.Url}: {request.Failure}",
                request.Failure));
        }
    }

    private void CompleteRequest(IRequest request)
    {
        if (_inflightRequests.TryRemove(request, out var completed))
            completed.Completion.TrySetResult();
    }

    private void ObserveRequest(IRequest request)
    {
        _inflightRequests.TryAdd(request, CreateObservedRequest(request));
    }

    private static ObservedRequest CreateObservedRequest(IRequest request)
    {
        request.Headers.TryGetValue("referer", out var initiatorUrl);
        return new ObservedRequest(
            initiatorUrl ?? request.Frame.Url,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    private static bool IsSupersededNavigationAbort(ObservedFailedRequest request) =>
        string.Equals(request.Failure, "net::ERR_ABORTED", StringComparison.Ordinal)
        // Limit the exception to document-owned resources. Failed fetch/XHR requests
        // remain fatal unless the journey registered their exact method and path.
        && request.ResourceType is "stylesheet" or "script" or "image" or "media" or "font" or "texttrack" or "manifest"
        && Uri.TryCreate(request.InitiatorUrl, UriKind.Absolute, out var initiator)
        && Uri.TryCreate(request.Page.Url, UriKind.Absolute, out var currentPage)
        && Uri.Compare(
            initiator,
            currentPage,
            UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
            UriFormat.Unescaped,
            StringComparison.OrdinalIgnoreCase) != 0;

    private static bool IsBrowserNetworkConsoleError(string message) =>
        message.StartsWith(
            "Failed to load resource: the server responded with a status of",
            StringComparison.Ordinal)
        || message.StartsWith("Failed to load resource: net::ERR_", StringComparison.Ordinal);

    private async Task WaitForNetworkQuiescenceAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (true)
            {
                var pending = _inflightRequests.Values.Select(request => request.Completion.Task).ToArray();
                if (pending.Length == 0)
                {
                    // Let request callbacks already queued by Playwright run before declaring
                    // the context quiet. Any request that starts during disposal is still
                    // observed and its failure is collected in the final snapshot below.
                    await Task.Yield();
                    if (_inflightRequests.IsEmpty)
                        return;
                    continue;
                }

                await Task.WhenAll(pending).WaitAsync(timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            var pending = _inflightRequests.Keys
                .Select(request => $"{request.Method} {request.Url}")
                .Order(StringComparer.Ordinal)
                .ToArray();
            throw new TimeoutException(
                $"Browser network did not become idle. Pending requests:{Environment.NewLine}"
                + string.Join(Environment.NewLine, pending));
        }
    }

    public async Task DisposeAsync()
    {
        var failures = new List<string>();
        if (_context is not null)
        {
            try
            {
                await WaitForNetworkQuiescenceAsync();
            }
            catch (Exception ex)
            {
                failures.Add($"Could not reach browser network quiescence: {ex.Message}");
            }

            if (_tracePath is not null)
            {
                try
                {
                    await _context.Tracing.StopAsync(new TracingStopOptions { Path = _tracePath });
                }
                catch (Exception ex)
                {
                    failures.Add($"Could not save Playwright trace '{_tracePath}': {ex.Message}");
                }
            }

            try
            {
                await _context.DisposeAsync();
            }
            catch (Exception ex)
            {
                failures.Add($"Could not dispose browser context: {ex.Message}");
            }
        }

        if (_browser is not null)
        {
            try
            {
                await _browser.DisposeAsync();
            }
            catch (Exception ex)
            {
                failures.Add($"Could not dispose browser: {ex.Message}");
            }
        }

        _playwright?.Dispose();
        failures.AddRange(SnapshotBrowserFailures(includeUnmetExpectations: true));
        Assert.True(
            failures.Count == 0,
            $"Browser diagnostics:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
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
        await using var noJavaScriptContext = await NewObservedContextAsync(new BrowserNewContextOptions
        {
            BaseURL = BaseUrl,
            JavaScriptEnabled = false,
            StorageState = state,
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
        });
        var noJavaScriptPage = await noJavaScriptContext.NewPageAsync();
        await noJavaScriptPage.GotoAsync("/Languages?returnUrl=%2FQuizzes");
        await noJavaScriptPage.GetByRole(AriaRole.Button, new() { Name = "Serbian (Latin)", Exact = true })
            .PressAsync("Enter");
        await Expect(noJavaScriptPage).ToHaveURLAsync(new Regex("/Quizzes$", RegexOptions.IgnoreCase));
    }

    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task RegisterLoginLogoutAndProtectedRedirect()
    {
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
        await Page.GotoAsync("/Quizzes");
        await Expect(Page).ToHaveURLAsync(new Regex("/Quizzes$", RegexOptions.IgnoreCase));
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Log out", Exact = true }))
            .ToBeVisibleAsync();
    }

    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task CreateQuizAddWordAndStartPractice()
    {
        await RegisterAndSelectPolishAsync();
        await CreateQuizWithWordAsync();

        await Page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("Start Quiz", RegexOptions.IgnoreCase) }).ClickAsync();
        var reverseDirection = Page.Locator("label.choice").Filter(new()
        {
            Has = Page.Locator("input[name='PracticeDirection'][value='target-to-source']"),
        });
        await reverseDirection.ClickAsync();
        await Expect(reverseDirection.Locator("input")).ToBeCheckedAsync();
        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Start Quiz", RegexOptions.IgnoreCase) }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex("/FlashcardQuiz", RegexOptions.IgnoreCase));
        await Expect(Page.Locator("[data-flashcard-session]")).ToBeVisibleAsync();
        var revealForm = Page.Locator("[data-card-reveal-form]");
        var revealButton = revealForm.Locator(":scope > .flashcard-clickable");
        var pronunciationButton = revealForm.Locator(":scope > button[data-tts]");
        await Expect(revealButton).ToBeVisibleAsync();
        await Expect(revealButton.Locator("[data-tts]")).ToHaveCountAsync(0);
        await Expect(pronunciationButton).ToBeVisibleAsync();
        await Expect(pronunciationButton).ToHaveAttributeAsync("aria-pressed", "false");
        await pronunciationButton.FocusAsync();
        await Expect(pronunciationButton).ToBeFocusedAsync();
    }

    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task QuizDialogTrapsFocusClosesWithEscapeAndRestoresItsTrigger()
    {
        await RegisterAndSelectPolishAsync();
        var assistantToggle = Page.Locator("[data-assistant-toggle]");
        var assistantWindow = Page.Locator("[data-assistant-window]");
        await assistantToggle.ClickAsync();
        await Expect(assistantWindow).ToBeVisibleAsync();

        var trigger = Page.GetByRole(AriaRole.Button, new() { Name = "New Collection", Exact = true });
        await trigger.FocusAsync();
        await trigger.PressAsync("Enter");

        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Create New Collection", Exact = true });
        var name = dialog.GetByLabel("Collection Name", new() { Exact = true });
        var create = dialog.GetByRole(AriaRole.Button, new() { Name = "Create Collection", Exact = true });
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(name).ToBeFocusedAsync();

        await Page.Keyboard.PressAsync("Shift+Tab");
        await Expect(create).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Tab");
        await Expect(name).ToBeFocusedAsync();

        await Page.Keyboard.PressAsync("Escape");
        await Expect(dialog).ToBeHiddenAsync();
        await Expect(assistantWindow).ToBeVisibleAsync();
        await Expect(assistantToggle).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(trigger).ToBeFocusedAsync();
    }

    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task QuizDropRecoversFromHttpAndNetworkFailures()
    {
        await RegisterAndSelectPolishAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "New Collection", Exact = true }).ClickAsync();
        await Page.GetByLabel("Collection Name", new() { Exact = true }).FillAsync("Drop target");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Create Collection", Exact = true }).Last.ClickAsync();

        await Page.GotoAsync("/Quizzes");
        await Page.GetByRole(AriaRole.Button, new() { Name = "New Quiz", Exact = true }).ClickAsync();
        await Page.GetByLabel("Quiz Name", new() { Exact = true }).FillAsync("Movable quiz");
        await Page.GetByLabel("Source Language", new() { Exact = true })
            .SelectOptionAsync(new SelectOptionValue { Label = "English" });
        await Page.GetByRole(AriaRole.Button, new() { Name = "Create Quiz", Exact = true }).Last.ClickAsync();
        await Page.GotoAsync("/Quizzes");

        var attempts = 0;
        string? moveRequestUrl = null;
        await Page.RouteAsync("**/Quizzes/MoveQuizToCollection", async route =>
        {
            attempts++;
            moveRequestUrl = route.Request.Url;
            if (attempts == 1)
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 503,
                    ContentType = "application/problem+json",
                    Body = "{\"detail\":\"Moving is temporarily unavailable.\"}",
                });
                return;
            }

            await route.AbortAsync();
        });
        ExpectHttpFailure("POST", "/Quizzes/MoveQuizToCollection", 503);

        var card = Page.Locator("[data-quiz-card]").Filter(new() { HasText = "Movable quiz" });
        var target = Page.Locator("[data-collection-drop-target]").Filter(new() { HasText = "Drop target" });
        var message = Page.Locator("[data-quiz-library-message]");

        async Task DropQuizAsync()
        {
            var quizId = await card.GetAttributeAsync("data-quiz-id");
            await target.EvaluateAsync("(element, id) => { const data = new DataTransfer(); data.setData('text/plain', id); element.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: data })); }", quizId);
        }

        await DropQuizAsync();
        await Expect(message).ToContainTextAsync("Moving is temporarily unavailable.");
        Assert.Contains("MoveQuizToCollection", moveRequestUrl, StringComparison.OrdinalIgnoreCase);
        await Expect(target).Not.ToHaveClassAsync(new Regex("is-drop-saving"));

        await message.EvaluateAsync("element => { element.hidden = true; element.textContent = ''; }");
        ExpectRequestFailure("POST", "/Quizzes/MoveQuizToCollection");
        await DropQuizAsync();
        await Expect(message).ToContainTextAsync("Could not move that quiz.");
        await Expect(message).ToBeVisibleAsync();
        await Expect(target).Not.ToHaveClassAsync(new Regex("is-drop-saving"));
    }

    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task PreviewAndImportExternalAiJsonHierarchy()
    {
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

    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task CreateLinkStudyAndInspectAnkiCollection()
    {
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

    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task CreateSaveAndPlayCustomQuiz()
    {
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

    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task CreateRenameSwitchAndDeleteAssistantChatsWithoutAi()
    {
        await RegisterAndSelectPolishAsync();
        await Page.GotoAsync("/Quizzes");
        await Page.Locator("[data-assistant-toggle]").ClickAsync();
        var chatsPane = Page.Locator("[data-assistant-pane='chats']");

        async Task OpenChatsAsync()
        {
            await Page.Locator("[data-assistant-tab='chats']").DispatchEventAsync("click");
            await Expect(chatsPane).ToBeVisibleAsync();
        }

        async Task DispatchAndAcceptDialogAsync(ILocator trigger, string? promptText = null)
        {
            Task? acceptTask = null;
            void AcceptDialog(object? _, IDialog dialog) => acceptTask = dialog.AcceptAsync(promptText);

            Page.Dialog += AcceptDialog;
            try
            {
                await trigger.DispatchEventAsync("click");
                if (acceptTask is null)
                    throw new InvalidOperationException("The action completed without opening a dialog.");
                await acceptTask;
            }
            finally
            {
                Page.Dialog -= AcceptDialog;
            }
        }

        // The chat pane is visible in the initial markup, so visibility alone is not a
        // readiness barrier. Wait until first-chat selection and history loading finish;
        // otherwise their final pane switch can overwrite the Chats click below.
        await Expect(Page.Locator("[data-assistant-panel]"))
            .ToHaveAttributeAsync("data-assistant-initialized", "true");
        await AssertNoPageErrorsAsync();
        await OpenChatsAsync();
        await Expect(Page.Locator("[data-assistant-new-chat]")).ToBeVisibleAsync();
        await Expect(Page.Locator("[data-assistant-chat-item]")).ToHaveCountAsync(1);

        // The handler immediately switches back to the chat pane, hiding this button. A
        // direct DOM click avoids Playwright retrying its actionability checks after the
        // successful handler has already started that transition.
        await Page.Locator("[data-assistant-new-chat]").DispatchEventAsync("click");
        // Creating a chat renders the list once when the POST completes and again after
        // selection/history loading. Wait for the operation's final pane transition so
        // the Chats click below cannot race that second render and replace the row while
        // Playwright is hovering it.
        await Expect(Page.Locator("[data-assistant-pane='chat']")).ToBeVisibleAsync();
        await OpenChatsAsync();
        await Expect(Page.Locator("[data-assistant-chat-item]")).ToHaveCountAsync(2);

        await DispatchAndAcceptDialogAsync(
            Page.Locator("[data-assistant-chat-item]").First
                .Locator("button[aria-label='Rename chat']"),
            "Employer demo chat");
        await Expect(Page.Locator("[data-assistant-chat-item]").First).ToContainTextAsync("Employer demo chat");

        // Renaming replaces the list rows asynchronously. Reassert the pane before
        // interacting with the replacement row so a delayed selection transition
        // cannot leave Playwright targeting a row inside the hidden Chats pane.
        await OpenChatsAsync();
        var deleteResponseTask = Page.WaitForResponseAsync(response =>
            response.Request.Method.Equals("DELETE", StringComparison.OrdinalIgnoreCase)
            && new Uri(response.Url).AbsolutePath.StartsWith("/Assistant/Chats/", StringComparison.Ordinal));
        await DispatchAndAcceptDialogAsync(
            Page.Locator("[data-assistant-chat-item]").First
                .Locator("button[aria-label='Delete chat']"));
        var deleteResponse = await deleteResponseTask;
        Assert.Equal(200, deleteResponse.Status);
        await Expect(Page.Locator("[data-assistant-chat-item]")).ToHaveCountAsync(1);
        await Expect(Page.Locator("[data-assistant-pane='chat']")).ToBeVisibleAsync();
        await OpenChatsAsync();
        await Page.Locator(".assistant-chat-main").DispatchEventAsync("click");
        await Expect(Page.Locator("[data-assistant-pane='chat']")).ToBeVisibleAsync();
    }

    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task AssistantApplySelectsReturnedQuizWithoutCallingBearerQuizApi()
    {
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
                threadId = new Uri(route.Request.Url).Segments[^2].Trim('/'),
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
                ExpectHttpFailure("PATCH", new Uri(route.Request.Url).AbsolutePath, 503);
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
        await Expect(Page.Locator("[data-assistant-pane='chat']")).ToBeVisibleAsync();
        var assistantInput = Page.Locator("[data-assistant-textarea]");
        var assistantSubmit = Page.Locator("[data-assistant-submit]");
        await Expect(assistantInput).ToBeEditableAsync();
        await Expect(assistantSubmit).ToBeEnabledAsync();
        await assistantInput.FillAsync("Create a travel quiz");
        await assistantSubmit.ClickAsync();
        await Expect(Page.Locator("[data-assistant-pending-card]")).ToBeVisibleAsync();

        var pendingCard = Page.Locator("[data-assistant-pending-card]");
        // The card has to name a standard quiz and account for both content types, so an
        // unwanted custom quiz or a silently dropped set of sentences is visible before Apply.
        await Expect(pendingCard).ToContainTextAsync("5 words and 5 sentences");
        await Expect(pendingCard).Not.ToContainTextAsync("custom quiz");
        var applyButton = pendingCard.GetByRole(AriaRole.Button, new() { Name = "Apply", Exact = true });
        var rejectButton = pendingCard.GetByRole(AriaRole.Button, new() { Name = "Reject", Exact = true });
        ExpectHttpFailure("POST", $"/Assistant/Apply/{assistantMessageId}", 409);
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

    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task SwedishDisplayLanguagePersistsAcrossNavigationReloadMobileAndSignIn()
    {
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

        await using var otherDevice = await NewObservedContextAsync(new BrowserNewContextOptions { BaseURL = BaseUrl });
        var signedInPage = await otherDevice.NewPageAsync();
        await signedInPage.GotoAsync("/login");
        await signedInPage.GetByLabel("Email Address").FillAsync(email);
        await signedInPage.GetByLabel("Password", new() { Exact = true }).FillAsync(password);
        await signedInPage.GetByRole(AriaRole.Button, new() { Name = "Log In" }).ClickAsync();
        await Expect(signedInPage.Locator("html")).ToHaveAttributeAsync("lang", "sv-SE");

        await signedInPage.GotoAsync("/Languages");
        await signedInPage.Locator("button[name='language'][value='pl']").ClickAsync();
        foreach (var route in new[] { "/", "/Quizzes", "/Books", "/Anki", "/Explore", "/Transcripts" })
        {
            await signedInPage.GotoAsync(route);
            await Expect(signedInPage.Locator("html")).ToHaveAttributeAsync("lang", "sv-SE");
        }
    }

    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task EveryDisplayLanguageSwitchesPersistsAndKeepsPublicRoutesAccessible()
    {
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
        await WaitForNetworkQuiescenceAsync();
        var failures = SnapshotBrowserFailures(includeUnmetExpectations: false);
        Assert.True(
            failures.Count == 0,
            $"Browser errors:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private void ExpectHttpFailure(string method, string path, int status) =>
        AddExpectedFailure(new BrowserFailureExpectation(
            BrowserFailureKind.HttpResponse,
            method,
            path,
            status));

    private void ExpectRequestFailure(string method, string path) =>
        AddExpectedFailure(new BrowserFailureExpectation(
            BrowserFailureKind.RequestFailed,
            method,
            path,
            Status: null));

    private void AddExpectedFailure(BrowserFailureExpectation expectation)
    {
        if (!expectation.Path.StartsWith("/", StringComparison.Ordinal))
            throw new ArgumentException("Expected browser failure paths must be absolute paths.", nameof(expectation));

        lock (_expectedFailuresLock)
        {
            _expectedFailures.Add(expectation);
        }
    }

    private bool ConsumeExpectedFailure(BrowserFailureKind kind, IRequest request, int? status)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
            return false;

        lock (_expectedFailuresLock)
        {
            var index = _expectedFailures.FindIndex(expectation =>
                expectation.Kind == kind
                && string.Equals(expectation.Method, request.Method, StringComparison.OrdinalIgnoreCase)
                && string.Equals(expectation.Path, uri.AbsolutePath, StringComparison.Ordinal)
                && expectation.Status == status);
            if (index < 0)
                return false;

            _expectedFailures.RemoveAt(index);
            return true;
        }
    }

    private List<string> SnapshotBrowserFailures(bool includeUnmetExpectations)
    {
        var failures = _pageErrors
            .Concat(_consoleErrors)
            .Concat(_responseErrors)
            .Concat(_requestErrors
                .Where(request => !IsSupersededNavigationAbort(request))
                .Select(request => request.Description))
            .ToList();
        if (!includeUnmetExpectations)
            return failures;

        lock (_expectedFailuresLock)
        {
            failures.AddRange(_expectedFailures.Select(expectation =>
                $"EXPECTED FAILURE DID NOT OCCUR: {expectation}"));
        }

        return failures;
    }

    private enum BrowserFailureKind
    {
        HttpResponse,
        RequestFailed,
    }

    private sealed record BrowserFailureExpectation(
        BrowserFailureKind Kind,
        string Method,
        string Path,
        int? Status)
    {
        public override string ToString() => Status is null
            ? $"{Kind} {Method} {Path}"
            : $"{Kind} {Status} {Method} {Path}";
    }

    private sealed record ObservedRequest(
        string InitiatorUrl,
        TaskCompletionSource Completion);

    private sealed record ObservedFailedRequest(
        IPage Page,
        string InitiatorUrl,
        string ResourceType,
        string Description,
        string? Failure);
}
