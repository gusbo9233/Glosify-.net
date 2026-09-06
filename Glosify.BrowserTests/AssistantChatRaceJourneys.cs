using System.Text.Json;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace Glosify.BrowserTests;

public sealed partial class PortfolioJourneys
{
    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task AssistantChatRace_DelayedHistoryCannotReplaceAnotherSelectionOrAReopenedChat()
    {
        var first = new TaskCompletionSource<IRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
        var older = new TaskCompletionSource<IRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aRequests = 0;
        await SetupChatRaceAsync(async route =>
        {
            if (route.Request.Url.Contains("/chat-a/", StringComparison.Ordinal))
            {
                var attempt = Interlocked.Increment(ref aRequests);
                if (attempt == 1) { first.SetResult(route); return; }
                if (attempt == 2) { older.SetResult(route); return; }
                await FulfillHistoryAsync(route, "Latest A history");
            }
            else await FulfillHistoryAsync(route, "B history");
        });
        await Page.Locator("[data-assistant-toggle]").ClickAsync();
        var firstRoute = await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await SelectRaceChatAsync("Chat B");
        await Expect(RaceTranscript).ToContainTextAsync("B history");
        await FulfillAndDrainAsync(firstRoute, "Stale initial A history");
        await Expect(RaceTranscript).ToContainTextAsync("B history");
        await Expect(RaceTranscript).Not.ToContainTextAsync("Stale initial A history");

        await SelectRaceChatAsync("Chat A");
        var olderRoute = await older.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await SelectRaceChatAsync("Chat B");
        await Expect(RaceTranscript).ToContainTextAsync("B history");
        await SelectRaceChatAsync("Chat A");
        await Expect(RaceTranscript).ToContainTextAsync("Latest A history");
        await FulfillAndDrainAsync(olderRoute, "Stale reopened A history");
        await Expect(RaceTranscript).ToContainTextAsync("Latest A history");
        await Expect(RaceTranscript).Not.ToContainTextAsync("Stale reopened A history");
    }

    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task AssistantChatRace_DelayedReplyCannotAlterAnotherChatsTranscriptOrPendingControls()
    {
        var sendA = new TaskCompletionSource<IRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendB = new TaskCompletionSource<IRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aReplyStored = false;
        await SetupChatRaceAsync(route => FulfillHistoryAsync(route,
            route.Request.Url.Contains("/chat-a/", StringComparison.Ordinal)
                ? aReplyStored ? "Reply for A" : "A history" : "B history"));
        await Page.RouteAsync("**/Assistant/Chats/*/Send", route =>
        {
            (route.Request.Url.Contains("/chat-a/", StringComparison.Ordinal) ? sendA : sendB).SetResult(route);
            return Task.CompletedTask;
        });
        await OpenRaceAssistantAsync();
        await SendRaceMessageAsync("Question A");
        var a = await sendA.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await SelectRaceChatAsync("Chat B");
        await Expect(RaceTranscript).ToContainTextAsync("B history");
        await Expect(RaceSubmit).ToBeEnabledAsync();
        await SendRaceMessageAsync("Question B");
        var b = await sendB.Task.WaitAsync(TimeSpan.FromSeconds(10));
        aReplyStored = true;
        await FulfillReplyAndDrainAsync(a, "Reply for A");
        await Expect(RaceTranscript).Not.ToContainTextAsync("Reply for A");
        await Expect(RaceTranscript).ToContainTextAsync("Question B");
        await Expect(RaceSubmit).ToBeDisabledAsync();
        await Expect(Page.Locator("[data-assistant-status]")).ToHaveTextAsync("Thinking...");
        await FulfillReplyAndDrainAsync(b, "Reply for B");
        await Expect(RaceTranscript).ToContainTextAsync("Reply for B");
        await Expect(RaceSubmit).ToBeEnabledAsync();
        await SelectRaceChatAsync("Chat A");
        await Expect(RaceTranscript).ToContainTextAsync("Reply for A");
        await Expect(RaceTranscript).Not.ToContainTextAsync("Reply for B");
    }

    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task AssistantChatRace_ReopenedChatRefreshesWhenItsPendingReplyFinishes()
    {
        var send = new TaskCompletionSource<IRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stored = false;
        await SetupChatRaceAsync(route => FulfillHistoryAsync(route,
            route.Request.Url.Contains("/chat-a/", StringComparison.Ordinal)
                ? stored ? "Stored A reply" : "A history" : "B history"));
        await Page.RouteAsync("**/Assistant/Chats/*/Send", route =>
        {
            send.SetResult(route);
            return Task.CompletedTask;
        });
        await OpenRaceAssistantAsync();
        await SendRaceMessageAsync("Question A");
        var pending = await send.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await SelectRaceChatAsync("Chat B");
        await Expect(RaceTranscript).ToContainTextAsync("B history");
        await SelectRaceChatAsync("Chat A");
        await Expect(RaceTranscript).ToContainTextAsync("A history");
        await Expect(RaceSubmit).ToBeDisabledAsync();
        stored = true;
        await FulfillReplyAndDrainAsync(pending, "Stored A reply");
        await Expect(RaceTranscript).ToContainTextAsync("Stored A reply");
        await Expect(RaceTranscript.Locator(".assistant-message")).ToHaveCountAsync(1);
        await Expect(RaceSubmit).ToBeEnabledAsync();
    }

    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task AssistantChatRace_SubmissionWaitsForTheSelectedChatsHistory()
    {
        var history = new TaskCompletionSource<IRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;
        await SetupChatRaceAsync(route =>
        {
            if (route.Request.Url.Contains("/chat-b/", StringComparison.Ordinal))
            {
                history.SetResult(route);
                return Task.CompletedTask;
            }
            return FulfillHistoryAsync(route, "A history");
        });
        await Page.RouteAsync("**/Assistant/Chats/*/Send", route =>
        {
            Interlocked.Increment(ref sends);
            Assert.Contains("/chat-b/Send", route.Request.Url);
            return route.FulfillAsync(new()
            {
                ContentType = "application/json",
                Body = JsonSerializer.Serialize(new { assistantMessageId = "reply-b", assistantText = "Reply B",
                    toolEvents = Array.Empty<object>(), pendingChanges = Array.Empty<object>(), status = "active" }),
            });
        });
        await OpenRaceAssistantAsync();
        await SelectRaceChatAsync("Chat B");
        var pending = await history.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Page.Locator("[data-assistant-textarea]").FillAsync("Question B");
        await Page.Locator("[data-assistant-form]").DispatchEventAsync("submit");
        await DrainBrowserTasksAsync();
        Assert.Equal(0, sends);
        await FulfillAndDrainAsync(pending, "B history");
        await Expect(RaceTranscript).ToContainTextAsync("Question B");
        await Expect(RaceTranscript).ToContainTextAsync("Reply B");
        Assert.Equal(1, sends);
    }

    private ILocator RaceTranscript => Page.Locator("[data-assistant-transcript]");
    private ILocator RaceSubmit => Page.Locator("[data-assistant-submit]");

    private async Task SetupChatRaceAsync(Func<IRoute, Task> history)
    {
        await Page.RouteAsync("**/Assistant/Chats", route => route.FulfillAsync(new()
        {
            ContentType = "application/json",
            Body = JsonSerializer.Serialize(new { chats = new[]
            {
                new { id = "chat-a", title = "Chat A", preview = "", updatedAt = DateTimeOffset.UtcNow },
                new { id = "chat-b", title = "Chat B", preview = "", updatedAt = DateTimeOffset.UtcNow },
            }}),
        }));
        await Page.RouteAsync("**/Assistant/Chats/*/History", history);
        await RegisterAndSelectPolishAsync();
        await Page.GotoAsync("/Quizzes");
    }

    private async Task OpenRaceAssistantAsync()
    {
        await Page.Locator("[data-assistant-toggle]").ClickAsync();
        await Expect(Page.Locator("[data-assistant-panel]"))
            .ToHaveAttributeAsync("data-assistant-initialized", "true");
        await Expect(RaceSubmit).ToBeEnabledAsync();
    }

    private Task SelectRaceChatAsync(string title) => Page.Locator(".assistant-chat-main")
        .Filter(new() { HasText = title }).DispatchEventAsync("click");

    private async Task SendRaceMessageAsync(string text)
    {
        await Page.Locator("[data-assistant-textarea]").FillAsync(text);
        await RaceSubmit.ClickAsync();
    }

    private static Task FulfillHistoryAsync(IRoute route, string text) => route.FulfillAsync(new()
    {
        ContentType = "application/json",
        Body = JsonSerializer.Serialize(new { messages = new[]
        {
            new { id = text, role = "model", text, toolEvents = Array.Empty<object>(), pendingChanges = Array.Empty<object>(), status = "active" },
        }}),
    });

    private async Task FulfillAndDrainAsync(IRoute route, string text)
    {
        var response = Page.WaitForResponseAsync(response => response.Url == route.Request.Url);
        await FulfillHistoryAsync(route, text);
        await (await response).FinishedAsync();
        await DrainBrowserTasksAsync();
    }

    private async Task FulfillReplyAndDrainAsync(IRoute route, string text)
    {
        var response = Page.WaitForResponseAsync(response => response.Url == route.Request.Url);
        await route.FulfillAsync(new()
        {
            ContentType = "application/json",
            Body = JsonSerializer.Serialize(new { assistantMessageId = text, assistantText = text,
                toolEvents = Array.Empty<object>(), pendingChanges = Array.Empty<object>(), status = "active" }),
        });
        await (await response).FinishedAsync();
        await DrainBrowserTasksAsync();
    }

    private Task DrainBrowserTasksAsync() => Page.EvaluateAsync(
        "() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");
}
