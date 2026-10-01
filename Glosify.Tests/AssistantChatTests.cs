using System.Text.Json;
using Glosify.Models.Entities;
using Glosify.Models.Library;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Assistant.Runtime;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

/// <summary>
/// The chat façade the web panel and the mobile API use: threads, history, request-reply
/// sends served by the run runtime, the classic Apply flow, and feedback.
/// </summary>
public sealed class AssistantChatTests
{
    [Fact]
    public async Task Chats_are_listed_per_user_and_per_selected_language()
    {
        await using var h = await AssistantHarness.CreateAsync(db =>
        {
            db.AssistantThreads.Add(new AssistantThread { Id = Guid.NewGuid(), UserId = AssistantHarness.UserId, Language = "German", Title = "German chat" });
            db.AssistantThreads.Add(new AssistantThread { Id = Guid.NewGuid(), UserId = "other", Language = "Polish", Title = "Theirs" });
            db.AssistantThreads.Add(new AssistantThread { Id = Guid.NewGuid(), UserId = AssistantHarness.UserId, Language = "Polish", Title = "Legacy quiz thread", QuizId = Guid.Empty });
        });

        var polish = await h.OrchestrateAsync(chats => chats.ListChatsAsync(AssistantHarness.UserId));
        h.Language.CurrentLanguage = null;
        await using (var db = h.Db())
        {
            (await db.Users.SingleAsync(user => user.Id == AssistantHarness.UserId)).SelectedQuizLanguageCode = null;
            await db.SaveChangesAsync();
        }

        // With no language chosen anywhere there is nothing to scope to, and hiding every chat
        // would look like lost history.
        var all = await h.OrchestrateAsync(chats => chats.ListChatsAsync(AssistantHarness.UserId));

        Assert.Equal([h.ThreadId], polish.Select(chat => chat.Id));
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task A_request_reply_send_titles_the_chat_and_returns_the_reply()
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Model.ThenCall("add_items", new { words = new[] { new { word = "dom", translation = "house" } } }).ThenText("Added dom.");

        var response = await h.OrchestrateAsync(chats => chats.SendChatMessageAsync(h.ThreadId, AssistantHarness.UserId, "Add dom", h.QuizId));

        Assert.Equal("Added dom.", response.AssistantText);
        Assert.Equal(h.ThreadId, response.ThreadId);
        Assert.Equal("add_items", Assert.Single(response.ToolEvents).Name);
        Assert.Contains("dom", Assert.Single(response.ToolEvents).ArgsJson);
        var history = await h.OrchestrateAsync(chats => chats.GetChatHistoryAsync(h.ThreadId, AssistantHarness.UserId));
        Assert.Equal(Assert.Single(response.ToolEvents).ArgsJson,
            Assert.Single(history.Messages.SelectMany(message => message.ToolEvents)).ArgsJson);
        Assert.Empty(response.PendingChanges);
        await using var db = h.Db();
        Assert.Equal("Add dom", (await db.AssistantThreads.SingleAsync()).Title);
        Assert.Equal(2, await db.AssistantMessages.CountAsync());
        Assert.Equal("dom", (await db.Words.SingleAsync()).Lemma);
    }

    [Fact]
    public async Task A_request_reply_send_offers_deletions_for_the_classic_apply_button()
    {
        await using var h = await AssistantHarness.CreateAsync(db => db.Words.Add(new Word { Id = "w1", QuizId = Guid.Empty, Lemma = "dom", Translation = "house" }));
        h.Model.ThenCall("delete_items", new { word_ids = new[] { "w1" } }).ThenText("Apply to remove dom.");

        var response = await h.OrchestrateAsync(chats => chats.SendMessageAsync(h.QuizId, AssistantHarness.UserId, "Remove dom"));

        Assert.Equal("Remove dom -> house", Assert.Single(response.PendingChanges).Summary);
        Assert.Equal(AssistantMessageStatus.Active, response.Status);
        var applied = await h.OrchestrateAsync(chats => chats.ApplyPendingChangesAsync(response.AssistantMessageId, AssistantHarness.UserId));
        Assert.Equal(1, applied.Applied);
        await using var db = h.Db();
        Assert.Empty(await db.Words.ToListAsync());
        Assert.Equal(AssistantMessageStatus.Applied, (await db.AssistantMessages.SingleAsync(message => message.Id == response.AssistantMessageId)).Status);
    }

    [Fact]
    public async Task A_chat_from_another_language_is_refused()
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Language.CurrentLanguage = "German";

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.OrchestrateAsync(chats => chats.SendChatMessageAsync(h.ThreadId, AssistantHarness.UserId, "Continue")));

        Assert.Contains("another language", error.Message);
    }

    [Fact]
    public async Task A_language_switch_starts_a_fresh_default_thread()
    {
        await using var h = await AssistantHarness.CreateAsync();
        var polish = await h.OrchestrateAsync(chats => chats.SendGlobalMessageAsync(AssistantHarness.UserId, "Explain the instrumental case"));
        h.Language.CurrentLanguage = "German";

        var german = await h.OrchestrateAsync(chats => chats.SendGlobalMessageAsync(AssistantHarness.UserId, "Explain the dative case"));

        Assert.NotEqual(polish.ThreadId, german.ThreadId);
        await using var db = h.Db();
        Assert.Equal("German", (await db.AssistantThreads.SingleAsync(thread => thread.Id == german.ThreadId)).Language);
        Assert.Equal(2, await db.AssistantMessages.CountAsync(message => message.ThreadId == german.ThreadId));
    }

    [Fact]
    public async Task A_chat_already_working_reports_a_request_in_progress()
    {
        await using var h = await AssistantHarness.CreateAsync();
        await h.StartAsync("First");

        await Assert.ThrowsAsync<AssistantTurnInProgressException>(() =>
            h.OrchestrateAsync(chats => chats.SendChatMessageAsync(h.ThreadId, AssistantHarness.UserId, "Second")));
    }

    [Fact]
    public async Task The_chat_falls_back_to_its_book_and_the_page_being_read_reaches_the_model()
    {
        var bookId = Guid.NewGuid();
        await using var h = await AssistantHarness.CreateAsync(db =>
        {
            db.BookDocuments.Add(new BookDocument { Id = bookId, UserId = AssistantHarness.UserId, Title = "Course book", PageCount = 2, BlobName = "b", OriginalFileName = "b.pdf" });
            db.BookPages.Add(new BookPage { Id = Guid.NewGuid(), BookDocumentId = bookId, PageNumber = 2, Text = "Ala ma kota." });
        });
        await using (var db = h.Db())
        {
            (await db.AssistantThreads.SingleAsync()).ContextBookDocumentId = bookId;
            await db.SaveChangesAsync();
        }

        await h.OrchestrateAsync(chats => chats.SendChatMessageAsync(
            h.ThreadId, AssistantHarness.UserId, "Translate this page", h.QuizId, documentContext: new AssistantDocumentContext(bookId, 2)));

        var note = AssistantRunTests.TextOf(h.Model.Requests[0].History[0]);
        Assert.Contains("Selected book: \"Course book\"", note);
        Assert.Contains("reading page 2 of \"Course book\"", note);
        Assert.Contains("Ala ma kota.", note);
        Assert.DoesNotContain("Ala ma kota.", h.Model.Requests[0].SystemInstruction);
    }

    [Fact]
    public async Task History_shows_a_runs_parts_and_offers_undo_on_its_final_reply()
    {
        await using var h = await AssistantHarness.CreateAsync(db => db.AssistantMessages.Add(new AssistantMessage
        {
            Id = Guid.NewGuid(),
            ThreadId = Guid.Empty,
            Sequence = -1,
            Role = AssistantMessageRole.Model,
            ContentJson = JsonSerializer.Serialize(new { parts = new[] { new { kind = "text", text = "A reply saved before runs existed." } } }),
            CreatedAt = DateTimeOffset.UtcNow,
        }));
        h.Model.ThenCall("add_items", new { words = new[] { new { word = "dom", translation = "house" } } }).ThenText("Added dom.");
        var run = await h.RunAsync("Add dom");

        var history = await h.OrchestrateAsync(chats => chats.GetChatHistoryAsync(h.ThreadId, AssistantHarness.UserId));

        Assert.Equal("A reply saved before runs existed.", history.Messages[0].Text);
        Assert.Null(history.Messages[0].Parts);
        var reply = history.Messages.Single(message => message.Id == run.MessageId);
        Assert.Equal(run.Id, reply.RunId);
        Assert.True(reply.CanUndo);
        Assert.True(reply.CanRate);
        Assert.Equal(["tool", "text"], reply.Parts!.Select(part => part.Type));
        Assert.Equal("Added dom.", reply.Text);
    }

    [Fact]
    public async Task Feedback_attaches_to_the_final_reply_and_is_owned()
    {
        await using var h = await AssistantHarness.CreateAsync();
        var response = await h.OrchestrateAsync(chats => chats.SendChatMessageAsync(h.ThreadId, AssistantHarness.UserId, "Help me."));

        await h.OrchestrateAsync(chats => chats.SaveFeedbackAsync(response.TurnId, AssistantHarness.UserId, AssistantFeedbackRating.Up, ["helpful"], "Nice"));
        await Assert.ThrowsAsync<AssistantTurnNotFoundException>(() =>
            h.OrchestrateAsync(chats => chats.SaveFeedbackAsync(response.TurnId, "other", "up", [], null)));

        var history = await h.OrchestrateAsync(chats => chats.GetChatHistoryAsync(h.ThreadId, AssistantHarness.UserId));
        var final = Assert.Single(history.Messages, message => message.CanRate);
        Assert.Equal(response.AssistantMessageId, final.Id);
        Assert.Equal("up", final.Feedback?.Rating);
    }

    [Fact]
    public async Task Deleting_a_chat_removes_its_runs_parts_and_journal()
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Model.ThenCall("add_items", new { words = new[] { new { word = "dom", translation = "house" } } }).ThenText("Added.");
        await h.RunAsync("Add dom");

        await h.OrchestrateAsync(async chats =>
        {
            await chats.DeleteChatAsync(h.ThreadId, AssistantHarness.UserId);
            return 0;
        });

        await using var db = h.Db();
        Assert.Empty(await db.AssistantMessages.ToListAsync());
        Assert.Empty(await db.AssistantRuns.ToListAsync());
        Assert.Empty(await db.AssistantParts.ToListAsync());
        Assert.Empty(await db.AssistantChanges.ToListAsync());
        Assert.Single(await db.Words.ToListAsync());
    }
}
