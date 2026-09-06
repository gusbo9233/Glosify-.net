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

    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task AssistantChatRace_ContextConfirmationCannotReplaceAnotherChatsLoadingStatus()
    {
        var patches = new Queue<TaskCompletionSource<IRoute>>();
        var histories = new Queue<TaskCompletionSource<IRoute>>();
        await SetupChatRaceAsync(route =>
        {
            if (route.Request.Url.Contains("/chat-b/", StringComparison.Ordinal))
            {
                histories.Dequeue().SetResult(route);
                return Task.CompletedTask;
            }
            return FulfillHistoryAsync(route, "A history");
        });
        await Page.RouteAsync("**/Assistant/Chats/chat-a", route =>
        {
            patches.Dequeue().SetResult(route);
            return Task.CompletedTask;
        });
        await OpenRaceAssistantAsync();
        foreach (var selector in new[] { "quiz", "material" })
        {
            var patch = new TaskCompletionSource<IRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
            var history = new TaskCompletionSource<IRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
            patches.Enqueue(patch);
            histories.Enqueue(history);
            await Page.Locator($"[data-assistant-{selector}-selector]").SelectOptionAsync("");
            var pendingPatch = await patch.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await SelectRaceChatAsync("Chat B");
            var pendingHistory = await history.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var status = Page.Locator("[data-assistant-status]");
            var loading = await status.TextContentAsync();
            var response = Page.WaitForResponseAsync(response => response.Url == pendingPatch.Request.Url);
            await pendingPatch.FulfillAsync(new()
            {
                ContentType = "application/json",
                Body = JsonSerializer.Serialize(new { id = "chat-a", title = "Chat A", preview = "" }),
            });
            await (await response).FinishedAsync();
            await DrainBrowserTasksAsync();
            // Complete the held request even if the subsequent regression assertion fails.
            var statusAfterPatch = await status.TextContentAsync();
            await Expect(RaceSubmit).ToBeDisabledAsync();
            await FulfillAndDrainAsync(pendingHistory, "B history");
            Assert.False(string.IsNullOrWhiteSpace(loading));
            Assert.Equal(loading, statusAfterPatch);
            await SelectRaceChatAsync("Chat A");
            await Expect(RaceTranscript).ToContainTextAsync("A history");
        }
    }

    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task AssistantChatRace_NewChatHistoryCannotClearAnotherChatsLoadingStatus()
    {
        var created = new TaskCompletionSource<IRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
        var selected = new TaskCompletionSource<IRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
        await SetupChatRaceAsync(route =>
        {
            if (route.Request.Url.Contains("/chat-new/", StringComparison.Ordinal)) created.SetResult(route);
            else if (route.Request.Url.Contains("/chat-b/", StringComparison.Ordinal)) selected.SetResult(route);
            else return FulfillHistoryAsync(route, "A history");
            return Task.CompletedTask;
        });
        await Page.RouteAsync("**/Assistant/Chats", route => route.Request.Method == "POST"
            ? route.FulfillAsync(new()
            {
                ContentType = "application/json",
                Body = JsonSerializer.Serialize(new { id = "chat-new", title = "New chat", preview = "" }),
            })
            : route.FallbackAsync());
        await OpenRaceAssistantAsync();
        await Page.Locator("[data-assistant-new-chat]").DispatchEventAsync("click");
        var newHistory = await created.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await SelectRaceChatAsync("Chat B");
        var bHistory = await selected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var status = Page.Locator("[data-assistant-status]");
        var loading = await status.TextContentAsync();
        await FulfillAndDrainAsync(newHistory, "New chat history");
        var statusAfterHistory = await status.TextContentAsync();
        await Expect(RaceSubmit).ToBeDisabledAsync();
        await FulfillAndDrainAsync(bHistory, "B history");
        Assert.False(string.IsNullOrWhiteSpace(loading));
        Assert.Equal(loading, statusAfterHistory);
        await Expect(RaceTranscript).ToContainTextAsync("B history");
        await Expect(RaceTranscript).Not.ToContainTextAsync("New chat history");
    }

    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task AssistantChatRace_SubmissionDoesNotWaitForAnObsoleteInitialHistory()
    {
        var initial = new TaskCompletionSource<IRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
        await SetupChatRaceAsync(route =>
        {
            if (route.Request.Url.Contains("/chat-a/", StringComparison.Ordinal))
            {
                initial.SetResult(route);
                return Task.CompletedTask;
            }
            return FulfillHistoryAsync(route, "B history");
        });
        await Page.RouteAsync("**/Assistant/Chats/*/Send", route =>
        {
            Assert.Contains("/chat-b/Send", route.Request.Url);
            return route.FulfillAsync(new()
            {
                ContentType = "application/json",
                Body = JsonSerializer.Serialize(new { assistantMessageId = "reply-b", assistantText = "Reply B",
                    toolEvents = Array.Empty<object>(), pendingChanges = Array.Empty<object>(), status = "active" }),
            });
        });
        await Page.Locator("[data-assistant-toggle]").ClickAsync();
        var pending = await initial.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await SelectRaceChatAsync("Chat B");
        await Expect(RaceTranscript).ToContainTextAsync("B history");
        await Expect(RaceSubmit).ToBeEnabledAsync();
        try
        {
            await SendRaceMessageAsync("Question B");
            await Expect(RaceTranscript).ToContainTextAsync("Reply B");
        }
        finally
        {
            await FulfillAndDrainAsync(pending, "Stale initial A history");
        }
        await Expect(RaceTranscript).ToContainTextAsync("Question B");
        await Expect(RaceTranscript).ToContainTextAsync("Reply B");
        await Expect(RaceTranscript).Not.ToContainTextAsync("Stale initial A history");
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
