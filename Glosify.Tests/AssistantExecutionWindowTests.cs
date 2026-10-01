using Glosify.Models.Entities;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Assistant.Runtime;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantExecutionWindowTests
{
    [Fact]
    public Task Productive_edits_continue_across_execution_windows() => VerifyProductiveWorkAsync();

    [SqlServerFact]
    public Task SqlServer_productive_edits_continue_across_execution_windows() =>
        SqlServerTestDatabase.RunAsync("productive_assistant", db => VerifyProductiveWorkAsync(db.Database.GetConnectionString()));

    private static async Task VerifyProductiveWorkAsync(string? connection = null)
    {
        await using var h = connection is null ? await AssistantHarness.CreateAsync() : await AssistantHarness.CreateSqlServerAsync(connection);
        h.Options.MaxModelCalls = 3;
        h.Restart();
        for (var index = 0; index < 6; index++)
            h.Model.ThenCall("add_items", new { quiz_id = (string?)null, words = new[] { new { word = $"word-{index}", translation = $"translation-{index}" } }, sentences = (object?)null });
        h.Model.ThenText("All six saved.");
        var run = await h.RunAsync("Add six entries in batches.");
        Assert.Equal(AssistantRunStatus.Completed, run.Status);
        Assert.Equal(6, run.SavedChanges);
        Assert.Equal(7, h.Model.Calls);
        Assert.All(h.Model.Requests, request => Assert.Equal(AgentToolChoice.Auto, request.ToolChoice));
        await using var db = h.Db();
        Assert.Equal(6, await db.Words.CountAsync());
    }

    [Fact]
    public async Task Saved_work_renews_an_expired_time_window()
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Options.WindowSeconds = 10;
        h.Restart();
        h.Model.ThenCall("add_items", new { words = new[] { new { word = "dom", translation = "house" } } })
            .ThenText("Saved.");
        var run = await h.StartAsync("Add dom.");
        await h.StepAsync(); // initialize
        await h.StepAsync(); // model
        await h.StepAsync(); // save
        h.Clock.Advance(TimeSpan.FromSeconds(11));
        await h.DrainAsync();
        var completed = await h.ViewAsync(run.Id);
        Assert.Equal(AssistantRunStatus.Completed, completed.Status);
        Assert.Equal(1, completed.SavedChanges);
        Assert.Equal(2, h.Model.Calls);
    }

    [Fact]
    public async Task Alternating_unchanged_reads_do_not_count_as_progress()
    {
        await using var h = await AssistantHarness.CreateAsync();
        var page = 0;
        h.Model.Fallback = _ => ScriptedModel.Reply("", ("list_items", new { kind = "words", offset = page++ % 2 }));
        var run = await h.RunAsync("Review this quiz.");
        Assert.Equal(AssistantRunStatus.Paused, run.Status);
        Assert.Contains("no progress", run.Reason);
        Assert.InRange(h.Model.Calls, 3, 8);
        Assert.Contains(h.Model.Requests, request => request.TrailingInstruction?.Contains("reread unchanged data") == true);
        Assert.Equal(0, run.SavedChanges);
    }

    [Fact]
    public async Task Compaction_keeps_distinct_quiz_pages_and_source_together()
    {
        await using var h = await AssistantHarness.CreateAsync();
        await using var db = h.Db();
        var conversation = new AssistantConversation(db, new AssistantMessagePresenter(), Options.Create(h.Options), AssistantToolFactory.Create(db).Toolbox);
        var runId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        AssistantPart Read(int sequence, string name, string args, int size) => new()
        {
            Id = Guid.NewGuid(), MessageId = messageId, RunId = runId, Type = AssistantPartTypes.Tool,
            ToolName = name, InputJson = args, Output = new string('x', size), State = AssistantToolStates.Completed,
            Step = sequence, Sequence = sequence,
        };
        var parts = new[]
        {
            Read(1, "list_items", "{\"offset\":0}", 17739),
            Read(2, "list_items", "{\"offset\":0}", 17739),
            Read(3, "list_items", "{\"offset\":100}", 14791),
            Read(4, "list_items", "{\"offset\":200}", 5917),
            Read(5, "read_source", "{\"from_line\":1}", 5616),
        };
        var snapshot = new AssistantConversation.Snapshot([new AssistantMessage { Id = messageId }], parts.ToLookup(part => part.MessageId));
        var pruned = conversation.PlanPruning(snapshot, 30_000, runId);
        Assert.Contains(parts[0].Id, pruned);
        Assert.All(parts.Skip(1), part => Assert.DoesNotContain(part.Id, pruned));
    }

    [Fact]
    public async Task A_new_oversized_result_is_shown_before_it_can_be_pruned()
    {
        await using var h = await AssistantHarness.CreateAsync();
        await using var db = h.Db();
        var conversation = new AssistantConversation(db, new AssistantMessagePresenter(), Options.Create(h.Options), AssistantToolFactory.Create(db).Toolbox);
        var part = new AssistantPart { Id = Guid.NewGuid(), MessageId = Guid.NewGuid(), RunId = Guid.NewGuid(),
            Step = 1, Type = AssistantPartTypes.Tool, ToolName = "list_items", State = AssistantToolStates.Completed,
            Output = new string('x', 90_000) };
        var snapshot = new AssistantConversation.Snapshot([new AssistantMessage { Id = part.MessageId }], new[] { part }.ToLookup(p => p.MessageId));
        Assert.Empty(conversation.PlanPruning(snapshot, 30_000, part.RunId!.Value));
    }
}
