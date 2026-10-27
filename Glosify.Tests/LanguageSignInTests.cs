using System.Net;
using AngleSharp.Html.Parser;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Assistant.Runtime;
using Glosify.Services.Language;
using Glosify.Services.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class LanguageSignInTests
{
    private const string Email = "language-learner@example.test";
    private const string Password = "Language-test-123!";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuestSelection_SurvivesLoginAndReachesTheBackgroundAssistant(bool dialog)
    {
        using var app = new AvatarFixture();
        await CreateLearnerAsync(app);
        using var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

        await SelectAsync(client, "Ukrainian");
        await AssertPickerAsync(client, "Ukrainian");
        await LoginAsync(client, dialog);

        await AssertPickerAndAssistantAsync(app, client, "Ukrainian", "uk");

        // A later choice must survive the next login, without replaying the old guest cookie.
        await SelectAsync(client, "French");
        var token = await TokenAsync(client, "/");
        var logout = await client.PostAsync("/Account/Logout", Form(token));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        await LoginAsync(client, dialog);
        await AssertPickerAndAssistantAsync(app, client, "French", "fr");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-language")]
    public async Task LoginWithoutAValidGuestChoice_RestoresSavedAccountLanguage(string? guestLanguage)
    {
        using var app = new AvatarFixture();
        await CreateLearnerAsync(app);
        using var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        // Simulate a stale browser selection as well as an absent/cleared/invalid guest choice.
        client.DefaultRequestHeaders.Add("Cookie", "glosify.language=Ukrainian"
            + (guestLanguage is null ? "" : $"; glosify.guest-language={guestLanguage}"));

        await LoginAsync(client, dialog: false);
        await AssertPickerAndAssistantAsync(app, client, "German", "de");
    }

    [Fact]
    public async Task FailedLogin_DoesNotConsumeGuestSelectionOrChangeTheAccount()
    {
        using var app = new AvatarFixture();
        await CreateLearnerAsync(app);
        using var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        await SelectAsync(client, "Ukrainian");
        var token = await TokenAsync(client, "/login");
        var response = await client.PostAsync("/login", Form(token, ("Email", Email), ("Password", "wrong-password")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertPickerAsync(client, "Ukrainian");
        using (var scope = app.Services.CreateScope())
        {
            var preferences = scope.ServiceProvider.GetRequiredService<IQuizLanguagePreferenceService>();
            Assert.Equal("de", (await preferences.GetSelectedAsync(Email))?.Code);
        }
        await LoginAsync(client, dialog: false);
        await AssertPickerAndAssistantAsync(app, client, "Ukrainian", "uk");
    }

    [Fact]
    public async Task DisplayCultureRefresh_DoesNotReplayAnOldGuestChoice()
    {
        using var app = new AvatarFixture();
        await CreateLearnerAsync(app);
        using var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        await LoginAsync(client, dialog: false);
        client.DefaultRequestHeaders.Add("Cookie", "glosify.guest-language=Ukrainian");

        var token = await TokenAsync(client, "/");
        var response = await client.PostAsync("/display-language", Form(token, ("culture", "sv-SE"), ("returnUrl", "/")));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using (var scope = app.Services.CreateScope())
        {
            var user = await scope.ServiceProvider.GetRequiredService<GlosifyContext>().Users.SingleAsync();
            Assert.Equal("sv-SE", user.DisplayCulture);
        }
        await AssertPickerAndAssistantAsync(app, client, "German", "de");
    }

    [Fact]
    public async Task SharedDemoSignIn_DoesNotPersistAVisitorsGuestChoice()
    {
        using var app = new AvatarFixture();
        await CreateLearnerAsync(app);
        // Enable the shared demo after seeding the account with a known preference.
        var options = app.Services.GetRequiredService<IOptions<DemoAccountOptions>>().Value;
        options.Enabled = true;
        options.Email = Email;
        options.Password = Password;
        options.AccessCode = "language-demo-test";
        options.Credits = 1;
        using var first = app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        await SelectAsync(first, "Ukrainian");
        Assert.Equal(HttpStatusCode.Redirect, (await first.GetAsync("/demo?code=language-demo-test")).StatusCode);
        await AssertPickerAndAssistantAsync(app, first, "German", "de");

        using var second = app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Redirect, (await second.GetAsync("/demo?code=language-demo-test")).StatusCode);
        await AssertPickerAndAssistantAsync(app, second, "German", "de");
    }

    private static async Task CreateLearnerAsync(AvatarFixture app)
    {
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var result = await users.CreateAsync(new ApplicationUser
        {
            Id = Email, UserName = Email, Email = Email, EmailConfirmed = true,
            SelectedQuizLanguageCode = "de",
        }, Password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Description)));
    }

    private static async Task SelectAsync(HttpClient client, string language)
    {
        var token = await TokenAsync(client, "/Languages");
        var response = await client.PostAsync("/Languages/Select", Form(token, ("language", language)));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task LoginAsync(HttpClient client, bool dialog)
    {
        var path = dialog ? "/login?dialog=true&returnUrl=%2F" : "/login";
        var token = await TokenAsync(client, path);
        var response = await client.PostAsync(path, Form(token, ("Email", Email), ("Password", Password)));
        Assert.Equal(dialog ? HttpStatusCode.OK : HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith(".AspNetCore.Identity.Application=", StringComparison.Ordinal));
    }

    private static async Task AssertPickerAndAssistantAsync(AvatarFixture app, HttpClient client, string name, string code)
    {
        await AssertPickerAsync(client, name);
        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/"));
        Assert.Equal(code, page.QuerySelector("[data-assistant-panel]")!.GetAttribute("data-learning-language-code"));
        // The worker has a fresh DI scope and no HTTP request or browser cookies.
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GlosifyContext>();
        Assert.Equal(code, await db.Users.Where(user => user.Id == Email).Select(user => user.SelectedQuizLanguageCode).SingleAsync());
        var thread = new AssistantThread { Id = Guid.NewGuid(), UserId = Email, ConversationLanguage = "English" };
        var run = new AssistantRun { Id = Guid.NewGuid(), ThreadId = thread.Id, UserId = Email };
        var resolved = await scope.ServiceProvider.GetRequiredService<AssistantRunContext>()
            .ResolveAsync(run, new AssistantRunInput("Help me practice"), thread, CancellationToken.None);
        Assert.Equal(name, resolved.Facts.TargetLanguage);
        Assert.Equal(code, resolved.Facts.TargetLanguageCode);
        Assert.Contains($"Learning language: {name}", resolved.ContextNote);
    }

    private static async Task AssertPickerAsync(HttpClient client, string language)
    {
        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/"));
        Assert.Equal(QuizLanguageCatalog.Find(language)!.Flag,
            page.QuerySelector(".language-context-pill .language-context-flag")!.TextContent.Trim());
    }

    private static async Task<string> TokenAsync(HttpClient client, string path)
    {
        var page = new HtmlParser().ParseDocument(await client.GetStringAsync(path));
        return page.QuerySelector("input[name='__RequestVerificationToken']")!.GetAttribute("value")!;
    }

    private static FormUrlEncodedContent Form(string token, params (string Key, string Value)[] values) =>
        new(values.Select(value => new KeyValuePair<string, string>(value.Key, value.Value))
            .Append(new KeyValuePair<string, string>("__RequestVerificationToken", token)));
}
