using System.Text.RegularExpressions;
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
        await Page.WaitForTimeoutAsync(250);
        await AssertNoPageErrorsAsync();
        await Page.Locator("[data-assistant-tab='chats']").ClickAsync();
        await Expect(Page.Locator(".assistant-chat-item")).ToHaveCountAsync(1);

        await Page.Locator("[data-assistant-new-chat]").ClickAsync();
        await Expect(Page.Locator(".assistant-chat-item")).ToHaveCountAsync(2);
        await Page.Locator("[data-assistant-tab='chats']").ClickAsync();

        async void RenameDialog(object? _, IDialog dialog) => await dialog.AcceptAsync("Employer demo chat");
        Page.Dialog += RenameDialog;
        await Page.Locator(".assistant-chat-item").First.HoverAsync();
        await Page.Locator(".assistant-chat-item").First
            .Locator("button[aria-label='Rename chat']")
            .ClickAsync();
        Page.Dialog -= RenameDialog;
        await Expect(Page.Locator(".assistant-chat-item").First).ToContainTextAsync("Employer demo chat");

        async void DeleteDialog(object? _, IDialog dialog) => await dialog.AcceptAsync();
        Page.Dialog += DeleteDialog;
        await Page.Locator(".assistant-chat-item").First.HoverAsync();
        await Page.Locator(".assistant-chat-item").First
            .Locator("button[aria-label='Delete chat']")
            .ClickAsync();
        Page.Dialog -= DeleteDialog;
        await Expect(Page.Locator(".assistant-chat-item")).ToHaveCountAsync(1);
        await Page.Locator("[data-assistant-tab='chats']").ClickAsync();
        await Page.Locator(".assistant-chat-main").ClickAsync();
        await Expect(Page.Locator("[data-assistant-pane='chat']")).ToBeVisibleAsync();
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
