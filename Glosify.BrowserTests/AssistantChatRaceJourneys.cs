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

    [BrowserFact]
    [Trait("Category", "Browser")]
    public Task AssistantChatRace_DelayedNewChatCannotReplaceAnotherSelection() =>
        VerifyDelayedNewChatAsync(200);

    [BrowserFact]
    [Trait("Category", "Browser")]
    public Task AssistantChatRace_DelayedNewChatFailureCannotAlterAnotherSelection() =>
        VerifyDelayedNewChatAsync(500);

    private async Task VerifyDelayedNewChatAsync(int statusCode)
    {
        var create = new TaskCompletionSource<IRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
        await SetupChatRaceAsync(route => FulfillHistoryAsync(route,
            route.Request.Url.Contains("/chat-b/", StringComparison.Ordinal) ? "B history" : "Other history"));
        await Page.RouteAsync("**/Assistant/Chats", route =>
        {
            if (route.Request.Method != "POST") return route.FallbackAsync();
            create.SetResult(route);
            return Task.CompletedTask;
        });
        await OpenRaceAssistantAsync();
        await Page.Locator("[data-assistant-new-chat]").DispatchEventAsync("click");
        var pending = await create.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await SelectRaceChatAsync("Chat B");
        await Expect(RaceTranscript).ToContainTextAsync("B history");
        var response = Page.WaitForResponseAsync(response =>
            response.Url == pending.Request.Url && response.Request.Method == "POST");
        if (statusCode >= 400) ExpectHttpFailure("POST", "/Assistant/Chats", statusCode);
        await pending.FulfillAsync(new()
        {
            Status = statusCode,
            ContentType = "application/json",
            Body = statusCode == 200
                ? JsonSerializer.Serialize(new { id = "chat-new", title = "New chat", preview = "" })
                : JsonSerializer.Serialize(new { title = "Create request failed", status = statusCode }),
        });
        await (await response).FinishedAsync();
        await DrainBrowserTasksAsync();
        await Expect(Page.Locator(".assistant-chat-item.is-active")).ToContainTextAsync("Chat B");
        await Expect(RaceTranscript).ToContainTextAsync("B history");
        await Expect(Page.Locator("[data-assistant-status]")).ToHaveTextAsync("");
        await Expect(RaceSubmit).ToBeEnabledAsync();
    }

    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task AssistantChatRace_NewChatWaitsUntilInitializationEstablishesASelection()
    {
        var catalog = new TaskCompletionSource<IRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialHistory = new TaskCompletionSource<IRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
        var creates = 0;
        await SetupChatRaceAsync(route =>
        {
            if (route.Request.Url.Contains("/chat-a/", StringComparison.Ordinal))
            {
                initialHistory.SetResult(route);
                return Task.CompletedTask;
            }
            return FulfillHistoryAsync(route, "New chat history");
        });
        await Page.RouteAsync("**/Assistant/Chats", route =>
        {
            if (route.Request.Method == "GET")
            {
                catalog.SetResult(route);
                return Task.CompletedTask;
            }
            Interlocked.Increment(ref creates);
            return route.FulfillAsync(new()
            {
                ContentType = "application/json",
                Body = JsonSerializer.Serialize(new { id = "chat-new", title = "New chat", preview = "" }),
            });
        });
        await Page.Locator("[data-assistant-toggle]").ClickAsync();
        var pending = await catalog.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var newChat = Page.Locator("[data-assistant-new-chat]");
        var disabledBeforeSelection = await newChat.IsDisabledAsync();
        await newChat.DispatchEventAsync("click");
        await DrainBrowserTasksAsync();
        var createsBeforeSelection = creates;
        await pending.FulfillAsync(new()
        {
            ContentType = "application/json",
            Body = JsonSerializer.Serialize(new { chats = new[]
            {
                new { id = "chat-a", title = "Chat A", preview = "", updatedAt = DateTimeOffset.UtcNow },
            }}),
        });
        var history = await initialHistory.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.True(disabledBeforeSelection);
            Assert.Equal(0, createsBeforeSelection);
            await Expect(newChat).ToBeEnabledAsync();
            await newChat.DispatchEventAsync("click");
            await Expect(RaceTranscript).ToContainTextAsync("New chat history");
        }
        finally
        {
            await FulfillAndDrainAsync(history, "Initial A history");
        }
        await Expect(Page.Locator(".assistant-chat-item.is-active")).ToContainTextAsync("New chat");
        await Expect(RaceTranscript).ToContainTextAsync("New chat history");
        Assert.Equal(1, creates);
    }

    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task AssistantChatRace_OlderCatalogCannotReplaceNewerChatMetadata()
    {
        var older = new TaskCompletionSource<IRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        await SetupChatRaceAsync(route => FulfillHistoryAsync(route, "History"));
        await OpenRaceAssistantAsync();
        await Page.RouteAsync("**/Assistant/Chats", route =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                older.SetResult(route);
                return Task.CompletedTask;
            }
            return FulfillRaceCatalogAsync(route, "Latest B preview");
        });
        await RouteRaceRepliesAsync();
        await SendRaceMessageAsync("Question A");
        var pending = await older.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await SelectRaceChatAsync("Chat B");
        await Expect(RaceSubmit).ToBeEnabledAsync();
        await SendRaceMessageAsync("Question B");
        await Expect(Page.Locator(".assistant-chat-main").Filter(new() { HasText = "Chat B" }))
            .ToContainTextAsync("Latest B preview");
        var response = Page.WaitForResponseAsync(response => response.Url == pending.Request.Url);
        await FulfillRaceCatalogAsync(pending, "Stale B preview");
        await (await response).FinishedAsync();
        await DrainBrowserTasksAsync();
        await Expect(Page.Locator(".assistant-chat-main").Filter(new() { HasText = "Chat B" }))
            .ToContainTextAsync("Latest B preview");
        await Expect(Page.Locator("[data-assistant-chat-list]")).Not.ToContainTextAsync("Stale B preview");
    }

    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task AssistantChatRace_OlderCatalogCannotRemoveANewlyCreatedChat()
    {
        var older = new TaskCompletionSource<IRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
        await SetupChatRaceAsync(route => FulfillHistoryAsync(route, "History"));
        await OpenRaceAssistantAsync();
        await Page.RouteAsync("**/Assistant/Chats", route =>
        {
            if (route.Request.Method == "GET")
            {
                older.SetResult(route);
                return Task.CompletedTask;
            }
            return route.FulfillAsync(new()
            {
                ContentType = "application/json",
                Body = JsonSerializer.Serialize(new { id = "chat-new", title = "New chat", preview = "" }),
            });
        });
        await RouteRaceRepliesAsync();
        await SendRaceMessageAsync("Question A");
        var pending = await older.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Page.Locator("[data-assistant-new-chat]").DispatchEventAsync("click");
        await Expect(Page.Locator(".assistant-chat-item.is-active")).ToContainTextAsync("New chat");
        var response = Page.WaitForResponseAsync(response => response.Url == pending.Request.Url);
        await FulfillRaceCatalogAsync(pending, "Old B preview");
        await (await response).FinishedAsync();
        await DrainBrowserTasksAsync();
        await Expect(Page.Locator(".assistant-chat-item.is-active")).ToContainTextAsync("New chat");
        await Expect(RaceTranscript).ToContainTextAsync("History");
    }

    private Task RouteRaceRepliesAsync() => Page.RouteAsync("**/Assistant/Chats/*/Send", route =>
        route.FulfillAsync(new()
        {
            ContentType = "application/json",
            Body = JsonSerializer.Serialize(new { assistantMessageId = "reply", assistantText = "Reply",
                toolEvents = Array.Empty<object>(), pendingChanges = Array.Empty<object>(), status = "active" }),
        }));

    private static Task FulfillRaceCatalogAsync(IRoute route, string bPreview) => route.FulfillAsync(new()
    {
        ContentType = "application/json",
        Body = JsonSerializer.Serialize(new { chats = new[]
        {
            new { id = "chat-a", title = "Chat A", preview = "", updatedAt = DateTimeOffset.UtcNow },
            new { id = "chat-b", title = "Chat B", preview = bPreview, updatedAt = DateTimeOffset.UtcNow },
        }}),
    });

    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task AssistantChatRace_PreSelectionSubmissionCannotSendLaterIntoAnotherChat()
    {
        var catalog = new TaskCompletionSource<IRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
        var initial = new TaskCompletionSource<IRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalogs = 0;
        var sent = new List<string>();
        await SetupChatRaceAsync(route =>
        {
            if (route.Request.Url.Contains("/chat-a/", StringComparison.Ordinal))
            {
                initial.SetResult(route);
                return Task.CompletedTask;
            }
            return FulfillHistoryAsync(route, "B history");
        });
        await Page.RouteAsync("**/Assistant/Chats", route =>
        {
            if (Interlocked.Increment(ref catalogs) != 1) return route.FallbackAsync();
            catalog.SetResult(route);
            return Task.CompletedTask;
        });
        await Page.RouteAsync("**/Assistant/Chats/*/Send", route =>
        {
            Assert.Contains("/chat-b/Send", route.Request.Url);
            using var payload = JsonDocument.Parse(route.Request.PostData!);
            sent.Add(payload.RootElement.GetProperty("message").GetString()!);
            return route.FulfillAsync(new()
            {
                ContentType = "application/json",
                Body = JsonSerializer.Serialize(new { assistantMessageId = "reply", assistantText = "Reply B",
                    toolEvents = Array.Empty<object>(), pendingChanges = Array.Empty<object>(), status = "active" }),
            });
        });
        await Page.Locator("[data-assistant-toggle]").ClickAsync();
        var pendingCatalog = await catalog.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var disabledBeforeSelection = await RaceSubmit.IsDisabledAsync();
        await Page.Locator("[data-assistant-textarea]").FillAsync("Before selection");
        await Page.Locator("[data-assistant-form]").DispatchEventAsync("submit");
        await FulfillRaceCatalogAsync(pendingCatalog, "");
        var pendingHistory = await initial.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await SelectRaceChatAsync("Chat B");
        await Expect(RaceSubmit).ToBeEnabledAsync();
        await SendRaceMessageAsync("Explicit B question");
        await Expect(RaceTranscript).ToContainTextAsync("Reply B");
        await Expect(RaceSubmit).ToBeEnabledAsync();
        await FulfillAndDrainAsync(pendingHistory, "Stale initial history");
        Assert.Equal("Explicit B question", Assert.Single(sent));
        Assert.True(disabledBeforeSelection);
        await Expect(RaceTranscript).Not.ToContainTextAsync("Before selection");
    }

    [BrowserFact]
    [Trait("Category", "Browser")]
    public Task AssistantChatRace_ReopenedChatShowsItsFailedReply() => VerifyReopenedReplyFailureAsync(false);

    [BrowserFact]
    [Trait("Category", "Browser")]
    public Task AssistantChatRace_ReopenedChatShowsItsNetworkFailure() => VerifyReopenedReplyFailureAsync(true);

    [BrowserFact]
    [Trait("Category", "Browser")]
    public Task AssistantChatRace_ReturningAfterAReplyFailedShowsItsError() => VerifyReopenedReplyFailureAsync(false, true);

    [BrowserFact]
    [Trait("Category", "Browser")]
    public Task AssistantChatRace_ReturningAfterANetworkFailureShowsItsError() => VerifyReopenedReplyFailureAsync(true, true);

    private async Task VerifyReopenedReplyFailureAsync(bool networkFailure, bool returnAfterFailure = false)
    {
        var send = new TaskCompletionSource<IRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
        await SetupChatRaceAsync(route => FulfillHistoryAsync(route, "Stored history"));
        await Page.RouteAsync("**/Assistant/Chats/*/Send", route =>
        {
            send.SetResult(route);
            return Task.CompletedTask;
        });
        await OpenRaceAssistantAsync();
        await SendRaceMessageAsync("Question A");
        var pending = await send.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await SelectRaceChatAsync("Chat B");
        await Expect(RaceSubmit).ToBeEnabledAsync();
        if (!returnAfterFailure)
        {
            await SelectRaceChatAsync("Chat A");
            await Expect(RaceTranscript).ToContainTextAsync("Stored history");
            await Expect(RaceSubmit).ToBeDisabledAsync();
        }
        if (networkFailure)
        {
            ExpectRequestFailure("POST", "/Assistant/Chats/chat-a/Send");
            await pending.AbortAsync("failed");
        }
        else
        {
            ExpectHttpFailure("POST", "/Assistant/Chats/chat-a/Send", 500);
            await pending.FulfillAsync(new()
            {
                Status = 500,
                ContentType = "application/problem+json",
                Body = "{\"status\":500,\"title\":\"Failed reply\"}",
            });
        }
        if (returnAfterFailure)
        {
            await DrainBrowserTasksAsync();
            await Expect(Page.Locator("[data-assistant-status]")).ToHaveTextAsync("");
            await SelectRaceChatAsync("Chat A");
        }
        await Expect(RaceSubmit).ToBeEnabledAsync();
        await Expect(Page.Locator("[data-assistant-status]")).ToHaveTextAsync(networkFailure
            ? "Network error talking to the assistant."
            : "The assistant could not respond.");
        await Expect(Page.Locator(".assistant-chat-item.is-active")).ToContainTextAsync("Chat A");
        await Expect(RaceTranscript).ToContainTextAsync("Stored history");
    }

    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task AssistantChatRace_FailedHistoryKeepsSubmissionDisabledUntilRetrySucceeds()
    {
        var fail = true;
        var sends = 0;
        await SetupChatRaceAsync(route =>
        {
            if (!fail) return FulfillHistoryAsync(route, "Recovered history");
            ExpectHttpFailure("GET", "/Assistant/Chats/chat-a/History", 500);
            return route.FulfillAsync(new() { Status = 500, ContentType = "application/json", Body = "{}" });
        });
        await Page.RouteAsync("**/Assistant/Chats/*/Send", route =>
        {
            Interlocked.Increment(ref sends);
            return route.FulfillAsync(new()
            {
                ContentType = "application/json",
                Body = JsonSerializer.Serialize(new { assistantMessageId = "reply", assistantText = "Reply",
                    toolEvents = Array.Empty<object>(), pendingChanges = Array.Empty<object>(), status = "active" }),
            });
        });
        await Page.Locator("[data-assistant-toggle]").ClickAsync();
        await Expect(Page.Locator("[data-assistant-panel]"))
            .ToHaveAttributeAsync("data-assistant-initialized", "true");
        await Page.Locator("[data-assistant-textarea]").FillAsync("Must not send");
        await Page.Locator("[data-assistant-form]").DispatchEventAsync("submit");
        await DrainBrowserTasksAsync();
        Assert.Equal(0, sends);
        await Expect(RaceSubmit).ToBeDisabledAsync();
        await Expect(Page.Locator("[data-assistant-status]"))
            .ToHaveTextAsync("Something went wrong. Please try again.");
        fail = false;
        await SelectRaceChatAsync("Chat A");
        await Expect(RaceTranscript).ToContainTextAsync("Recovered history");
        await Expect(RaceSubmit).ToBeEnabledAsync();
        await SendRaceMessageAsync("After recovery");
        await Expect(RaceTranscript).ToContainTextAsync("Reply");
        Assert.Equal(1, sends);
    }

    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task AssistantChatRace_ReopenedChatWaitsForItsPendingContextSave()
    {
        var patch = new TaskCompletionSource<IRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
        await SetupChatRaceAsync(route => FulfillHistoryAsync(route, "Stored history"));
        await Page.RouteAsync("**/Assistant/Chats/chat-a", route =>
        {
            patch.SetResult(route);
            return Task.CompletedTask;
        });
        string? sentQuiz = null;
        await Page.RouteAsync("**/Assistant/Chats/*/Send", route =>
        {
            using var payload = JsonDocument.Parse(route.Request.PostData!);
            sentQuiz = payload.RootElement.GetProperty("contextQuizId").GetString();
            return route.FulfillAsync(new()
            {
                ContentType = "application/json",
                Body = JsonSerializer.Serialize(new { assistantMessageId = "reply", assistantText = "Reply",
                    toolEvents = Array.Empty<object>(), pendingChanges = Array.Empty<object>(), status = "active" }),
            });
        });
        await OpenRaceAssistantAsync();
        const string quizId = "11111111-1111-1111-1111-111111111111";
        var picker = Page.Locator("[data-assistant-quiz-selector]");
        await Expect(picker).ToBeEnabledAsync();
        await picker.EvaluateAsync("(select, id) => { const option = new Option('New quiz', id); option.dataset.contextLabel = 'New quiz'; select.add(option); }", quizId);
        await picker.SelectOptionAsync(quizId);
        var pending = await patch.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await SelectRaceChatAsync("Chat B");
        await Expect(RaceSubmit).ToBeEnabledAsync();
        await SelectRaceChatAsync("Chat A");
        await DrainBrowserTasksAsync();
        var disabledBeforeSave = await RaceSubmit.IsDisabledAsync();
        var response = Page.WaitForResponseAsync(response => response.Url == pending.Request.Url);
        await pending.FulfillAsync(new()
        {
            ContentType = "application/json",
            Body = JsonSerializer.Serialize(new { id = "chat-a", title = "Chat A", preview = "",
                contextQuizId = quizId, contextQuizName = "New quiz" }),
        });
        await (await response).FinishedAsync();
        await DrainBrowserTasksAsync();
        await Expect(picker).ToHaveValueAsync(quizId);
        await Expect(RaceSubmit).ToBeEnabledAsync();
        await SendRaceMessageAsync("Use the new context");
        await Expect(RaceTranscript).ToContainTextAsync("Reply");
        Assert.Equal(quizId, sentQuiz);
        Assert.True(disabledBeforeSave);
    }

    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task AssistantChatRace_ContextConfirmationsPreservePendingAndFailedReplyStatuses()
    {
        var patches = new Queue<TaskCompletionSource<IRoute>>();
        var sends = new Queue<TaskCompletionSource<IRoute>>();
        await SetupChatRaceAsync(route => FulfillHistoryAsync(route, "History"));
        await Page.RouteAsync("**/Assistant/Chats/chat-a", route =>
        {
            patches.Dequeue().SetResult(route);
            return Task.CompletedTask;
        });
        await Page.RouteAsync("**/Assistant/Chats/chat-a/Send", route =>
        {
            sends.Dequeue().SetResult(route);
            return Task.CompletedTask;
        });
        await OpenRaceAssistantAsync();
        foreach (var selector in new[] { "quiz", "material" })
        foreach (var failReply in new[] { false, true })
        {
            var patch = new TaskCompletionSource<IRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
            var send = new TaskCompletionSource<IRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
            patches.Enqueue(patch);
            sends.Enqueue(send);
            await Page.Locator($"[data-assistant-{selector}-selector]").SelectOptionAsync("");
            var pendingPatch = await patch.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await SendRaceMessageAsync("Question");
            var pendingSend = await send.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (failReply)
            {
                ExpectHttpFailure("POST", "/Assistant/Chats/chat-a/Send", 500);
                await pendingSend.FulfillAsync(new()
                {
                    Status = 500, ContentType = "application/problem+json", Body = "{\"status\":500}",
                });
                await Expect(RaceSubmit).ToBeEnabledAsync();
            }
            var status = Page.Locator("[data-assistant-status]");
            var expected = failReply ? "The assistant could not respond." : "Thinking...";
            await Expect(status).ToHaveTextAsync(expected);
            var response = Page.WaitForResponseAsync(response => response.Url == pendingPatch.Request.Url);
            await pendingPatch.FulfillAsync(new()
            {
                ContentType = "application/json",
                Body = JsonSerializer.Serialize(new { id = "chat-a", title = "Chat A", preview = "" }),
            });
            await (await response).FinishedAsync();
            await DrainBrowserTasksAsync();
            var statusAfterPatch = await status.TextContentAsync();
            if (!failReply) await FulfillReplyAndDrainAsync(pendingSend, "Reply");
            Assert.Equal(expected, statusAfterPatch);
        }
    }

    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task AssistantChatRace_OnlyLatestQueuedContextWriteConfirmsItsValue()
    {
        var patches = new Queue<TaskCompletionSource<IRoute>>();
        await SetupChatRaceAsync(route => FulfillHistoryAsync(route, "History"));
        await Page.RouteAsync("**/Assistant/Chats/chat-a", route =>
        {
            patches.Dequeue().SetResult(route);
            return Task.CompletedTask;
        });
        await OpenRaceAssistantAsync();
        var picker = Page.Locator("[data-assistant-quiz-selector]");
        await Expect(picker).ToBeEnabledAsync();
        await picker.EvaluateAsync("select => { for (const id of ['quiz-one', 'quiz-two']) { const option = new Option(id, id); option.dataset.contextLabel = id; select.add(option); } }");
        var first = new TaskCompletionSource<IRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<IRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
        patches.Enqueue(first);
        patches.Enqueue(second);
        await picker.SelectOptionAsync("quiz-one");
        var pendingFirst = await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await picker.SelectOptionAsync("quiz-two");
        await pendingFirst.FulfillAsync(new()
        {
            ContentType = "application/json",
            Body = JsonSerializer.Serialize(new { id = "chat-a", title = "Chat A", preview = "",
                contextQuizId = "quiz-one", contextQuizName = "quiz-one" }),
        });
        var pendingSecond = await second.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await DrainBrowserTasksAsync();
        var intermediateStatus = await Page.Locator("[data-assistant-status]").TextContentAsync();
        await pendingSecond.FulfillAsync(new()
        {
            ContentType = "application/json",
            Body = JsonSerializer.Serialize(new { id = "chat-a", title = "Chat A", preview = "",
                contextQuizId = "quiz-two", contextQuizName = "quiz-two" }),
        });
        await Expect(Page.Locator("[data-assistant-status]")).ToHaveTextAsync("Quiz set to quiz-two.");
        Assert.Equal("", intermediateStatus);
        await Expect(picker).ToHaveValueAsync("quiz-two");
    }

    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task AssistantChatRace_ContextControlsWaitForReopenedContextRestoration()
    {
        var patch = new TaskCompletionSource<IRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
        var patchCount = 0;
        var createCount = 0;
        await SetupChatRaceAsync(route => FulfillHistoryAsync(route, "History"));
        await Page.RouteAsync("**/Assistant/Chats/chat-a", route =>
        {
            if (Interlocked.Increment(ref patchCount) == 1)
            {
                patch.SetResult(route);
                return Task.CompletedTask;
            }
            return route.FulfillAsync(new()
            {
                ContentType = "application/json",
                Body = JsonSerializer.Serialize(new { id = "chat-a", title = "Chat A", preview = "" }),
            });
        });
        await Page.RouteAsync("**/Assistant/Chats", route =>
        {
            if (route.Request.Method != "POST") return route.FallbackAsync();
            Interlocked.Increment(ref createCount);
            return route.FulfillAsync(new()
            {
                ContentType = "application/json",
                Body = JsonSerializer.Serialize(new { id = "chat-new", title = "New chat", preview = "" }),
            });
        });
        await OpenRaceAssistantAsync();
        var quiz = Page.Locator("[data-assistant-quiz-selector]");
        var material = Page.Locator("[data-assistant-material-selector]");
        var newChat = Page.Locator("[data-assistant-new-chat]");
        await quiz.SelectOptionAsync("");
        var pending = await patch.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await SelectRaceChatAsync("Chat B");
        await Expect(RaceSubmit).ToBeEnabledAsync();
        await SelectRaceChatAsync("Chat A");
        var quizDisabled = await quiz.IsDisabledAsync();
        var materialDisabled = await material.IsDisabledAsync();
        var newChatDisabled = await newChat.IsDisabledAsync();
        // Disabled controls must also reject directly dispatched events during restoration.
        await quiz.DispatchEventAsync("change");
        await material.DispatchEventAsync("change");
        await newChat.DispatchEventAsync("click");
        var response = Page.WaitForResponseAsync(response => response.Url == pending.Request.Url);
        await pending.FulfillAsync(new()
        {
            ContentType = "application/json",
            Body = JsonSerializer.Serialize(new { id = "chat-a", title = "Chat A", preview = "" }),
        });
        await (await response).FinishedAsync();
        await DrainBrowserTasksAsync();
        Assert.Equal(0, createCount);
        Assert.Equal(1, patchCount);
        Assert.True(quizDisabled);
        Assert.True(materialDisabled);
        Assert.True(newChatDisabled);
        await Expect(quiz).ToBeEnabledAsync();
        await Expect(material).ToBeEnabledAsync();
        await Expect(newChat).ToBeEnabledAsync();
        await Expect(Page.Locator(".assistant-chat-item.is-active")).ToContainTextAsync("Chat A");
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
