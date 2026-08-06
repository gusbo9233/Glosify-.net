using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Assistant;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public class AssistantPendingChangeStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public AssistantPendingChangeStoreTests()
    {
        // Relational provider: the store uses ExecuteUpdate, which in-memory cannot run.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    public void Dispose() => _connection.Dispose();

    // Apply replays changes in the order the tools were called, so the sequence has to
    // survive being written one tool call at a time.
    [Fact]
    public async Task Appended_changes_come_back_in_call_order()
    {
        await using var context = CreateContext();
        var store = new AssistantPendingChangeStore(context);
        var conversationId = Guid.NewGuid();

        await store.AppendAsync(conversationId, "user-1", null, Change("add_label", "first"));
        await store.AppendAsync(conversationId, "user-1", null, Change("add_text_input", "second"));
        await store.AppendAsync(conversationId, "user-1", null, Change("add_submit_button", "third"));

        var pending = await store.ListPendingAsync(conversationId, "user-1");

        Assert.Equal(
            ["add_label", "add_text_input", "add_submit_button"],
            pending.Select(change => change.Kind).ToArray());
        Assert.Equal("first", pending[0].Payload.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Another_users_conversation_is_never_returned()
    {
        await using var context = CreateContext();
        var store = new AssistantPendingChangeStore(context);
        var conversationId = Guid.NewGuid();

        await store.AppendAsync(conversationId, "user-1", null, Change("add_label", "a"));

        Assert.Empty(await store.ListPendingAsync(conversationId, "someone-else"));
        Assert.Single(await store.ListPendingAsync(conversationId, "user-1"));
    }

    [Fact]
    public async Task Attaching_to_a_message_lets_apply_settle_the_conversation()
    {
        await using var context = CreateContext();
        var store = new AssistantPendingChangeStore(context);
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        await store.AppendAsync(conversationId, "user-1", null, Change("add_label", "a"));
        await store.AppendAsync(conversationId, "user-1", null, Change("add_submit_button", "b"));

        var attached = await store.AttachToMessageAsync(conversationId, "user-1", messageId);
        var settled = await store.SettleAsync(messageId, "user-1", AssistantPendingChangeStatus.Applied);

        Assert.Equal(2, attached);
        Assert.Equal(2, settled);
        Assert.Empty(await store.ListPendingAsync(conversationId, "user-1"));
    }

    [Fact]
    public async Task Settling_does_not_touch_another_users_changes()
    {
        await using var context = CreateContext();
        var store = new AssistantPendingChangeStore(context);
        var messageId = Guid.NewGuid();
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        await store.AppendAsync(mine, "user-1", null, Change("add_label", "a"));
        await store.AppendAsync(theirs, "user-2", null, Change("add_label", "b"));
        await store.AttachToMessageAsync(mine, "user-1", messageId);
        await store.AttachToMessageAsync(theirs, "user-2", messageId);

        var settled = await store.SettleAsync(messageId, "user-1", AssistantPendingChangeStatus.Applied);

        Assert.Equal(1, settled);
        Assert.Single(await store.ListPendingAsync(theirs, "user-2"));
    }

    // One unreadable payload must not strand every other change in the conversation.
    [Fact]
    public async Task A_corrupt_payload_is_skipped_rather_than_blocking_the_rest()
    {
        await using var context = CreateContext();
        var store = new AssistantPendingChangeStore(context);
        var conversationId = Guid.NewGuid();
        await store.AppendAsync(conversationId, "user-1", null, Change("add_label", "good"));
        context.AssistantPendingChanges.Add(new AssistantPendingChange
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            UserId = "user-1",
            Sequence = 1,
            Kind = "add_text_input",
            PayloadJson = "{not json",
            Status = AssistantPendingChangeStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();

        var pending = await store.ListPendingAsync(conversationId, "user-1");

        Assert.Equal("add_label", Assert.Single(pending).Kind);
    }

    private static PendingChange Change(string kind, string id) =>
        new(kind, JsonSerializer.SerializeToElement(new { id, kind }));

    private GlosifyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseSqlite(_connection)
            .Options;
        var context = new GlosifyContext(options);
        context.Database.EnsureCreated();
        return context;
    }
}
