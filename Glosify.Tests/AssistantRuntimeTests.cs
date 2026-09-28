using System.Text.Json;
using System.Text.Json.Nodes;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Assistant.Runtime;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Anki;
using Glosify.Services.Language;
using Glosify.Services.Quizzes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantRuntimeTests
{
    [Fact]
    public async Task Checkpointed_call_survives_worker_restart_and_commits_once()
    {
        await using var h = await Harness.Create();
        await Checkpointed_call_survives_worker_restart_and_commits_once_scenario(h);
    }

    [SqlServerFact]
    public Task SqlServer_Checkpointed_call_survives_worker_restart_and_commits_once() => OnSqlServer("assistant-restart", Checkpointed_call_survives_worker_restart_and_commits_once_scenario);

    private static async Task Checkpointed_call_survives_worker_restart_and_commits_once_scenario(Harness h)
    {
        var task = await h.Start("Add the word dom");
        await h.Step(); // initialize
        await h.Step(); // provider response durably recorded
        Assert.Empty(await h.Db.Words.ToListAsync());
        Assert.Equal(1, h.Model.Calls);
        h.Restart();
        await h.Step(); // saved tool
        Assert.Equal("dom", (await h.Db.Words.SingleAsync()).Lemma);
        h.Restart();
        await h.Drain();
        Assert.Single(await h.Db.Words.ToListAsync());
        var view = await h.Store.ViewAsync(task.Id, "user", default);
        Assert.Equal("completed", view.Status);
        Assert.Equal(1, view.SavedChanges);
        Assert.Equal(2, h.Model.Calls);
        Assert.Equal("success", (await h.Db.AssistantTaskCalls.FirstAsync()).Status);
    }

    [Theory]
    [InlineData("Use list_quizzes to check whether my library contains any quizzes, then briefly report the result. Do not create, edit, move, or delete anything.")]
    [InlineData("No new quiz, just list my quizzes.")]
    [InlineData("Do not create a quiz, add, or edit anything; list my quizzes.")]
    [InlineData("Do not create a quiz, add and remove anything; list my quizzes.")]
    [InlineData("Do not create just because I mentioned a new quiz; list my quizzes.")]
    [InlineData("How do I create a quiz? First inspect my existing library.")]
    [InlineData("Add nothing; list my quizzes.")]
    [InlineData("Create no new quiz; list my quizzes.")]
    [InlineData("Start new topic: list my quizzes.")]
    public async Task Read_only_requests_complete_after_library_read_without_forced_writes(string message)
    {
        foreach (var prose in new[] { false, true })
        {
            await using var h = await Harness.Create();
            var thread = await h.Db.AssistantThreads.SingleAsync();
            thread.ContextQuizId = null;
            await h.Db.SaveChangesAsync();
            h.Model.ReportedTokens = 100;
            h.Model.Script = (_, n) => n == 1
                ? ("list_quizzes", "{\"language\":null}")
                : prose ? ("text", "Your library was checked.") : ("finish_task", "{\"summary\":\"Your library was checked.\"}");
            var task = await h.Store.StartAsync(h.ThreadId, "user", new("read-only", new(message)), default);
            await h.Drain(25);
            var result = await h.Store.ViewAsync(task.Id, "user", default);
            Assert.Equal("completed", result.Status);
            Assert.Equal(0, result.SavedChanges);
            Assert.Equal(2, result.ModelCalls);
            Assert.Contains(result.Activity, x => x.Tool == "list_quizzes" && x.Status == "success");
            Assert.Single(await h.Db.Quizzes.ToListAsync());
            Assert.Empty(await h.Db.Words.ToListAsync());
        }
    }

    [Theory]
    [InlineData("Add the word dom to this quiz.")]
    [InlineData("Please add the word dom to this quiz.")]
    public async Task Explicit_initial_addition_still_requires_a_saved_mutation(string message)
    {
        await using var h = await Harness.Create();
        h.Model.ReportedTokens = 100;
        h.Model.Script = (_, n) => n switch
        {
            1 => ("finish_task", "{\"summary\":\"Too early\"}"),
            2 => ("add_word", "{\"word\":\"dom\",\"translation\":\"house\"}"),
            _ => ("finish_task", "{\"summary\":\"Done\"}"),
        };
        var task = await h.Start(message);
        await h.Drain();
        Assert.Equal("completed", (await h.Store.ViewAsync(task.Id, "user", default)).Status);
        Assert.Equal(["correctable", "success"], await h.Db.AssistantTaskCalls.Where(x => x.ToolName == "finish_task")
            .OrderBy(x => x.Sequence).Select(x => x.Status).ToListAsync());
        Assert.Equal("dom", (await h.Db.Words.SingleAsync()).Lemma);
    }

    [Theory]
    [InlineData("Start new quiz called Travel.")]
    [InlineData("Make new quiz called Travel.")]
    public async Task Direct_new_quiz_command_requires_a_saved_quiz(string message)
    {
        await using var h = await Harness.Create();
        var thread = await h.Db.AssistantThreads.SingleAsync();
        thread.ContextQuizId = null;
        await h.Db.SaveChangesAsync();
        h.Model.ReportedTokens = 100;
        h.Model.Script = (_, n) => n switch
        {
            1 => ("finish_task", "{\"summary\":\"Too early\"}"),
            2 => ("create_vocabulary_quiz", "{\"name\":\"Travel\",\"source_language\":\"English\",\"target_language\":\"Polish\",\"complete\":true,\"words\":[{\"word\":\"dom\",\"translation\":\"house\"}],\"sentences\":[]}"),
            _ => ("finish_task", "{\"summary\":\"Done\"}"),
        };
        var task = await h.Store.StartAsync(h.ThreadId, "user", new("new-quiz", new(message)), default);
        await h.Drain();
        Assert.Equal("completed", (await h.Store.ViewAsync(task.Id, "user", default)).Status);
        Assert.Equal(["correctable", "success"], await h.Db.AssistantTaskCalls.Where(x => x.ToolName == "finish_task")
            .OrderBy(x => x.Sequence).Select(x => x.Status).ToListAsync());
        var quiz = await h.Db.Quizzes.SingleAsync(x => x.Name == "Travel");
        Assert.Equal("dom", (await h.Db.Words.SingleAsync(x => x.QuizId == quiz.Id)).Lemma);
    }

    [Theory]
    [InlineData("No new quiz just go ahead and add the word dom to this one.")]
    [InlineData("Do not create a quiz, add and explain the word dom to this one.")]
    public async Task Mixed_instructions_reach_the_model_intact_and_allow_requested_additions(string message)
    {
        await using var h = await Harness.Create();
        h.Model.ReportedTokens = 100;
        h.Model.Script = (request, n) =>
        {
            Assert.Contains(message, request.ContextInstruction);
            return n == 1 ? ("add_word", "{\"word\":\"dom\",\"translation\":\"house\"}")
                : ("finish_task", "{\"summary\":\"Done\"}");
        };
        var task = await h.Start(message);
        await h.Drain();
        Assert.Equal("completed", (await h.Store.ViewAsync(task.Id, "user", default)).Status);
        Assert.Equal("dom", (await h.Db.Words.SingleAsync()).Lemma);
    }

    [Fact]
    public async Task Idempotency_ownership_and_single_active_user_are_enforced()
    {
        await using var h = await Harness.Create();
        var input = new AssistantTaskStartInput("submission", new("Add dom", h.QuizId));
        var first = await h.Store.StartAsync(h.ThreadId, "user", input, default);
        var duplicate = await h.Store.StartAsync(h.ThreadId, "user", input, default);
        Assert.Equal(first.Id, duplicate.Id);
        await Assert.ThrowsAsync<AssistantTaskConflictException>(() => h.Store.StartAsync(h.ThreadId, "user", input with { Request = new("Different", h.QuizId) }, default));
        await Assert.ThrowsAsync<AssistantTaskConflictException>(() => h.Store.StartAsync(h.ThreadId, "user", input with { IdempotencyKey = "other" }, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Store.ViewAsync(first.Id, "other", default));
        Assert.Single(await h.Db.AssistantTasks.ToListAsync());
    }

    [Fact]
    public async Task Stale_worker_cannot_commit_after_lease_reassigned()
    {
        await using var h = await Harness.Create();
        var task = await h.Start("Add dom");
        var first = await h.Store.ClaimAsync(default);
        Assert.NotNull(first);
        h.Clock.Advance(TimeSpan.FromMinutes(3));
        var second = await h.Store.ClaimAsync(default);
        Assert.NotNull(second);
        await h.Executor.StepAsync(task.Id, first.Value.Lease, default);
        Assert.Empty(await h.Db.AssistantMessages.ToListAsync());
        await h.Executor.StepAsync(task.Id, second.Value.Lease, default);
        Assert.Single(await h.Db.AssistantMessages.ToListAsync());
    }

    [Fact]
    public async Task Steering_discards_checkpointed_but_unexecuted_calls()
    {
        await using var h = await Harness.Create();
        var task = await h.Start("Add dom");
        await h.Step(); await h.Step();
        var current = await h.Store.ViewAsync(task.Id, "user", default);
        h.Db.ChangeTracker.Clear();
        await h.Store.CommandAsync(task.Id, "user", "steer", new(current.Revision, "Do not add anything; just explain."), default);
        await h.Step();
        Assert.Empty(await h.Db.Words.ToListAsync());
        var state = RuntimeJson.Read<AssistantRuntimeState>((await h.Db.AssistantTasks.AsNoTracking().SingleAsync()).StateJson);
        Assert.Empty(state.Calls);
        Assert.Contains(state.History, x => x.ContentJson.Contains("superseded"));
        Assert.Equal(1, state.SteeringCount);
        await h.Drain();
        Assert.Equal("completed", (await h.Store.ViewAsync(task.Id, "user", default)).Status);
        Assert.Empty(await h.Db.Words.ToListAsync());
    }

    [Fact]
    public async Task Cancel_retains_saved_content_and_prevents_further_calls()
    {
        await using var h = await Harness.Create();
        var task = await h.Start("Add dom");
        await h.Step(); await h.Step(); await h.Step();
        h.Db.ChangeTracker.Clear();
        var current = await h.Store.ViewAsync(task.Id, "user", default);
        await h.Store.CommandAsync(task.Id, "user", "cancel", new(current.Revision), default);
        Assert.Null(await h.Store.ClaimAsync(default));
        Assert.Single(await h.Db.Words.ToListAsync());
        Assert.Equal(1, h.Model.Calls);
    }

    [Fact]
    public async Task Provider_failure_retries_are_persisted_and_bounded()
    {
        await using var h = await Harness.Create();
        h.Model.Failure = new GenerativeAiDependencyUnavailableException("temporary");
        var task = await h.Start("Add dom");
        await h.Step();
        for (var i = 0; i < 3; i++) { await h.Step(); h.Clock.Advance(TimeSpan.FromSeconds(10)); }
        var view = await h.Store.ViewAsync(task.Id, "user", default);
        Assert.Equal("paused", view.Status);
        Assert.Equal(3, h.Model.Calls);
        Assert.Equal(3, view.ModelCalls);
        Assert.Empty(await h.Db.Words.ToListAsync());
    }

    [Fact]
    public async Task Budget_pause_resumes_without_replaying_a_saved_mutation()
    {
        await using var h = await Harness.Create();
        h.Options.MaxModelCalls = 1;
        var task = await h.Start("Add dom");
        await h.Step(); await h.Step(); await h.Step(); await h.Step();
        var paused = await h.Store.ViewAsync(task.Id, "user", default);
        Assert.Equal("paused", paused.Status);
        Assert.Single(await h.Db.Words.ToListAsync());
        h.Db.ChangeTracker.Clear();
        await h.Store.CommandAsync(task.Id, "user", "resume", new(paused.Revision), default);
        await h.Drain();
        Assert.Equal("completed", (await h.Store.ViewAsync(task.Id, "user", default)).Status);
        Assert.Single(await h.Db.Words.ToListAsync());
    }

    [Fact]
    public async Task Deletion_requires_concrete_approval_and_approval_does_not_repeat_tool()
    {
        await using var h = await Harness.Create();
        await Deletion_requires_concrete_approval_and_approval_does_not_repeat_tool_scenario(h);
    }

    [SqlServerFact]
    public Task SqlServer_Deletion_requires_concrete_approval_and_approval_does_not_repeat_tool() => OnSqlServer("assistant-approval", Deletion_requires_concrete_approval_and_approval_does_not_repeat_tool_scenario);

    private static async Task Deletion_requires_concrete_approval_and_approval_does_not_repeat_tool_scenario(Harness h)
    {
        h.Db.Words.Add(new Word { Id = "word", QuizId = h.QuizId, Lemma = "dom", Translation = "house" });
        await h.Db.SaveChangesAsync();
        h.Model.FirstName = "delete_word";
        h.Model.FirstArgs = "{\"word_id\":\"word\"}";
        var task = await h.Start("Delete the word dom");
        await h.Step(); await h.Step(); await h.Step();
        var review = await h.Store.ViewAsync(task.Id, "user", default);
        Assert.Equal("awaiting_approval", review.Status);
        Assert.Single(review.ApprovalChanges);
        Assert.Contains("dom", review.ApprovalChanges[0].Summary);
        Assert.Single(await h.Db.Words.ToListAsync());
        h.Db.ChangeTracker.Clear();
        await h.Store.CommandAsync(task.Id, "user", "approve", new(review.Revision), default);
        await h.Step();
        Assert.Empty(await h.Db.Words.ToListAsync());
        Assert.Single(await h.Db.AssistantTaskCalls.ToListAsync());
    }

    [Fact]
    public async Task Concurrent_user_edit_is_not_overwritten()
    {
        await using var h = await Harness.Create();
        await Concurrent_user_edit_is_not_overwritten_scenario(h);
    }

    [SqlServerFact]
    public Task SqlServer_Concurrent_user_edit_is_not_overwritten() => OnSqlServer("assistant-concurrent-edit", Concurrent_user_edit_is_not_overwritten_scenario);

    private static async Task Concurrent_user_edit_is_not_overwritten_scenario(Harness h)
    {
        h.Db.Words.Add(new Word { Id = "word", QuizId = h.QuizId, Lemma = "dom", Translation = "house" });
        await h.Db.SaveChangesAsync();
        h.Model.FirstName = "edit_word";
        h.Model.FirstArgs = "{\"word_id\":\"word\",\"word\":\"domek\",\"translation\":\"cottage\"}";
        await h.Start("Edit dom to domek");
        await h.Step(); await h.Step();
        await h.Db.Words.Where(x => x.Id == "word").ExecuteUpdateAsync(s => s.SetProperty(x => x.Translation, "user edit"));
        await h.Step();
        Assert.Equal("user edit", (await h.Db.Words.AsNoTracking().SingleAsync()).Translation);
        Assert.Equal("conflict", (await h.Db.AssistantTaskCalls.SingleAsync()).Status);
        // Keeping the user's edit is a valid outcome, not an unresolved mutation error.
        await h.Drain();
        Assert.Equal("completed", (await h.Db.AssistantTasks.AsNoTracking().SingleAsync()).Status);
        Assert.Equal("user edit", (await h.Db.Words.AsNoTracking().SingleAsync()).Translation);
    }

    [Fact]
    public async Task User_edit_supersedes_the_rest_of_a_stale_batch()
    {
        await using var h = await Harness.Create();
        await User_edit_supersedes_the_rest_of_a_stale_batch_scenario(h);
    }

    [SqlServerFact]
    public Task SqlServer_User_edit_supersedes_the_rest_of_a_stale_batch() => OnSqlServer("assistant-stale-batch", User_edit_supersedes_the_rest_of_a_stale_batch_scenario);

    private static async Task User_edit_supersedes_the_rest_of_a_stale_batch_scenario(Harness h)
    {
        h.Db.Words.Add(new Word { Id = "w1", QuizId = h.QuizId, Lemma = "dom", Translation = "house" });
        h.Db.Words.Add(new Word { Id = "w2", QuizId = h.QuizId, Lemma = "kot", Translation = "cat" });
        await h.Db.SaveChangesAsync();
        const string editW1 = "{\"word_id\":\"w1\",\"word\":null,\"translation\":\"planned A\"}";
        h.Model.Batch = n => n switch
        {
            1 => [("edit_word", editW1), ("edit_word", "{\"word_id\":\"w2\",\"word\":null,\"translation\":\"planned B\"}")],
            // Replanned from current content: repeat the untouched word's edit, keep the user's.
            2 => [("edit_word", editW1)],
            _ => null,
        };
        var task = await h.Start("Fix both translations");
        await h.Step(); await h.Step(); // initialize, plan both edits
        await h.Db.Words.Where(x => x.Id == "w2").ExecuteUpdateAsync(s => s.SetProperty(x => x.Translation, "user edit"));
        await h.Step(); // the first call finds the quiz changed
        var state = RuntimeJson.Read<AssistantRuntimeState>((await h.Db.AssistantTasks.AsNoTracking().SingleAsync()).StateJson);
        Assert.Empty(state.Calls);
        Assert.Equal(2, state.CallSequence);
        // Both planned calls are answered, so the provider history stays paired.
        Assert.Contains("\"call-1-0\"", state.History[^1].ContentJson);
        Assert.Contains("\"call-1-1\"", state.History[^1].ContentJson);
        Assert.Contains("superseded", state.History[^1].ContentJson);
        Assert.Equal("conflict", (await h.Db.AssistantTaskCalls.AsNoTracking().SingleAsync()).Status);
        await h.Step(); // the model replans instead of the stale second call running
        Assert.Equal(2, h.Model.Calls);
        Assert.Equal(["house", "user edit"], await h.Db.Words.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Translation).ToListAsync());
        // A call rejected for a conflict never ran, so repeating it is not a duplicate.
        await h.Drain();
        Assert.Equal("completed", (await h.Store.ViewAsync(task.Id, "user", default)).Status);
        Assert.Equal(["planned A", "user edit"], await h.Db.Words.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Translation).ToListAsync());
        Assert.Equal(["edit_word:conflict", "edit_word:success", "finish_task:success"],
            await h.Db.AssistantTaskCalls.AsNoTracking().OrderBy(x => x.Sequence).Select(x => x.ToolName + ":" + x.Status).ToListAsync());
    }

    [Fact]
    public async Task Synchronous_task_finishes_after_a_proposal_corrects_a_rejected_one()
    {
        await using var h = await Harness.Create();
        h.Model.Script = (_, n) => n switch
        {
            1 => ("add_word", "{\"word\":\"dom\",\"translation\":\"\"}"),
            2 => ("get_quiz_summary", "{}"),
            3 => ("finish_task", "{\"summary\":\"Proposed dom.\"}"), // a read does not resolve the rejection
            4 => ("add_word", "{\"word\":\"dom\",\"translation\":\"house\"}"),
            _ => ("finish_task", "{\"summary\":\"Proposed dom.\"}"),
        };
        var task = await h.Store.StartAsync(h.ThreadId, "user", new("sync-correction", new("Add the word dom", h.QuizId)), default, manualApproval: true);
        await h.Drain(20);
        var view = await h.Store.ViewAsync(task.Id, "user", default);
        Assert.Equal("completed", view.Status);
        var calls = await h.Db.AssistantTaskCalls.AsNoTracking().OrderBy(x => x.Sequence).Select(x => x.ToolName + ":" + x.Status).ToListAsync();
        Assert.Equal(["add_word:correctable", "get_quiz_summary:proposed", "finish_task:correctable", "add_word:proposed", "finish_task:success"], calls);
        var response = RuntimeJson.Read<AssistantTurnResponse>(RuntimeJson.Write(view.Result));
        Assert.Equal(PendingChangeKinds.AddWord, Assert.Single(response.PendingChanges).Kind);
        Assert.Empty(await h.Db.Words.ToListAsync()); // Still a proposal for Apply.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Partial_batch_requires_every_skipped_target_to_be_corrected(bool manualApproval)
    {
        await using var h = await Harness.Create();
        h.Model.ReportedTokens = 100;
        h.Model.Script = (_, n) => n switch
        {
            1 => ("add_words", "{\"words\":[{\"word\":\"dom\",\"translation\":\"house\"},{\"word\":\"kot\",\"translation\":\"\"},{\"word\":\"woda\",\"translation\":\"\"}]}"),
            2 => ("add_word", "{\"word\":\"kot\",\"translation\":\"cat\"}"),
            3 => ("finish_task", "{\"summary\":\"Too early\"}"),
            4 => ("add_word", "{\"word\":\"las\",\"translation\":\"forest\"}"),
            5 => ("finish_task", "{\"summary\":\"Still too early\"}"),
            6 => ("add_word", "{\"word\":\"woda\",\"translation\":\"water\"}"),
            _ => ("finish_task", "{\"summary\":\"All corrected\"}"),
        };
        var task = await h.Store.StartAsync(h.ThreadId, "user", new("partial-corrections", new("Add these words", h.QuizId)), default, manualApproval);
        await h.Drain(25);
        var view = await h.Store.ViewAsync(task.Id, "user", default);
        Assert.Equal("completed", view.Status);
        var finishes = await h.Db.AssistantTaskCalls.AsNoTracking().Where(x => x.ToolName == "finish_task")
            .OrderBy(x => x.Sequence).Select(x => x.Status).ToListAsync();
        Assert.Equal(["correctable", "correctable", "success"], finishes);
        Assert.Equal(7, h.Model.Calls);
        if (manualApproval)
        {
            var response = RuntimeJson.Read<AssistantTurnResponse>(RuntimeJson.Write(view.Result));
            Assert.Equal(4, response.PendingChanges.Count);
            Assert.Empty(await h.Db.Words.ToListAsync());
        }
        else Assert.Equal(["dom", "kot", "las", "woda"], await h.Db.Words.OrderBy(x => x.Lemma).Select(x => x.Lemma).ToListAsync());
    }

    [Fact]
    public async Task Approving_a_partial_bulk_edit_keeps_skipped_items_unresolved()
    {
        await using var h = await Harness.Create();
        h.Db.Words.AddRange(Enumerable.Range(1, 3).Select(i => new Word
            { Id = "w" + i, QuizId = h.QuizId, Lemma = "word" + i, Translation = "old" }));
        await h.Db.SaveChangesAsync();
        h.Model.ReportedTokens = 100;
        h.Model.Script = (_, n) => n switch
        {
            1 => ("edit_words", "{\"changes\":[{\"word_id\":\"w1\",\"word\":null,\"translation\":\"one\"},{\"word_id\":\"w2\",\"word\":null,\"translation\":\"two\"},{\"word_id\":\"w3\",\"word\":null,\"translation\":\"\"}]}"),
            2 => ("finish_task", "{\"summary\":\"Too early\"}"),
            3 => ("edit_word", "{\"word_id\":\"w3\",\"word\":null,\"translation\":\"three\"}"),
            _ => ("finish_task", "{\"summary\":\"Done\"}"),
        };
        var task = await h.Start("Edit the three translations");
        await h.Drain();
        var pending = await h.Store.ViewAsync(task.Id, "user", default);
        Assert.Equal("awaiting_approval", pending.Status);
        h.Db.ChangeTracker.Clear();
        await h.Store.CommandAsync(task.Id, "user", "approve", new(pending.Revision), default);
        await h.Drain();
        Assert.Equal("completed", (await h.Store.ViewAsync(task.Id, "user", default)).Status);
        Assert.Equal(["correctable", "success"], await h.Db.AssistantTaskCalls.Where(x => x.ToolName == "finish_task")
            .OrderBy(x => x.Sequence).Select(x => x.Status).ToListAsync());
        Assert.Equal(["one", "two", "three"], await h.Db.Words.OrderBy(x => x.Id).Select(x => x.Translation).ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unidentified_rejected_items_require_distinct_later_correction_evidence(bool manualApproval)
    {
        await using var h = await Harness.Create();
        h.Model.ReportedTokens = 100;
        h.Model.Script = (request, n) =>
        {
            string Link(int sequence)
            {
                using var context = JsonDocument.Parse(request.ContextInstruction!.Split("\nTask state: ")[1]);
                var key = context.RootElement.GetProperty("unresolvedMutations").EnumerateObject().First().Name;
                return RuntimeJson.Write(new { mutation_key = key, saved_call = sequence.ToString() });
            }
            return n switch
            {
                1 => ("add_words", "{\"words\":[{\"word\":\"\",\"translation\":\"house\"},{\"word\":\"\",\"translation\":\"water\"}]}"),
                2 => ("add_word", "{\"word\":\"dom\",\"translation\":\"house\"}"),
                3 => ("resolve_rejected_item", Link(0)), // The rejected call is not evidence.
                4 => ("resolve_rejected_item", Link(1)),
                5 => ("resolve_rejected_item", Link(1)), // One correction cannot resolve two missing items.
                6 => ("finish_task", "{\"summary\":\"Too early\"}"),
                7 => ("add_word", "{\"word\":\"woda\",\"translation\":\"water\"}"),
                8 => ("resolve_rejected_item", Link(6)),
                _ => ("finish_task", "{\"summary\":\"Done\"}"),
            };
        };
        var task = await h.Store.StartAsync(h.ThreadId, "user", new("missing-identities", new("Add these words", h.QuizId)), default, manualApproval);
        await h.Drain(25);
        Assert.Equal("completed", (await h.Store.ViewAsync(task.Id, "user", default)).Status);
        Assert.Equal(["correctable", "success", "correctable", "success"], await h.Db.AssistantTaskCalls
            .Where(x => x.ToolName == "resolve_rejected_item").OrderBy(x => x.Sequence).Select(x => x.Status).ToListAsync());
        Assert.Equal(["correctable", "success"], await h.Db.AssistantTaskCalls
            .Where(x => x.ToolName == "finish_task").OrderBy(x => x.Sequence).Select(x => x.Status).ToListAsync());
        if (!manualApproval) Assert.Equal(["dom", "woda"], await h.Db.Words.OrderBy(x => x.Lemma).Select(x => x.Lemma).ToListAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Unrelated_saved_word_cannot_resolve_an_unidentified_rejection(bool manualApproval, bool duplicate)
    {
        await using var h = await Harness.Create();
        h.Model.ReportedTokens = 100;
        h.Model.Script = (request, n) =>
        {
            string Link(int sequence)
            {
                using var context = JsonDocument.Parse(request.ContextInstruction!.Split("\nTask state: ")[1]);
                var key = context.RootElement.GetProperty("unresolvedMutations").EnumerateObject().Single().Name;
                return RuntimeJson.Write(new { mutation_key = key, saved_call = sequence.ToString() });
            }
            return n switch
            {
                1 => ("add_words", RuntimeJson.Write(new { words = Enumerable.Repeat(new { word = "", translation = "house" }, duplicate ? 2 : 1).ToArray() })),
                2 => ("add_word", "{\"word\":\"las\",\"translation\":\"forest\"}"),
                3 => ("resolve_rejected_item", Link(1)),
                4 => ("finish_task", "{\"summary\":\"Too early\"}"),
                5 => ("add_word", "{\"word\":\"dom\",\"translation\":\"house\"}"),
                6 => ("resolve_rejected_item", Link(4)),
                _ => ("finish_task", "{\"summary\":\"Done\"}"),
            };
        };
        var task = await h.Store.StartAsync(h.ThreadId, "user", new("bound-correction", new("Add these words", h.QuizId)), default, manualApproval);
        await h.Drain(25);
        Assert.Equal("completed", (await h.Store.ViewAsync(task.Id, "user", default)).Status);
        Assert.Equal(["correctable", "success"], await h.Db.AssistantTaskCalls
            .Where(x => x.ToolName == "resolve_rejected_item").OrderBy(x => x.Sequence).Select(x => x.Status).ToListAsync());
        Assert.Equal(["correctable", "success"], await h.Db.AssistantTaskCalls
            .Where(x => x.ToolName == "finish_task").OrderBy(x => x.Sequence).Select(x => x.Status).ToListAsync());
        if (!manualApproval) Assert.Equal("house", (await h.Db.Words.SingleAsync(x => x.Lemma == "dom")).Translation);
    }

    [Fact]
    public async Task Intentional_word_sentence_deduplication_does_not_block_completed_draft()
    {
        await using var h = await Harness.Create();
        var thread = await h.Db.AssistantThreads.SingleAsync();
        thread.ContextQuizId = null;
        await h.Db.SaveChangesAsync();
        h.Model.ReportedTokens = 100;
        h.Model.Script = (request, n) =>
        {
            if (n == 1) return ("create_vocabulary_quiz", "{\"name\":\"Deduplicated\",\"source_language\":\"English\",\"target_language\":\"Polish\",\"complete\":false,\"words\":[{\"word\":\"Ala ma kota.\",\"translation\":\"Ala has a cat.\"}],\"sentences\":[{\"text\":\"Ala ma kota.\",\"translation\":\"Ala has a cat.\"}]}" );
            if (n == 2)
            {
                using var context = JsonDocument.Parse(request.ContextInstruction!.Split("\nTask state: ")[1]);
                return ("create_vocabulary_quiz", RuntimeJson.Write(new
                { draft_id = context.RootElement.GetProperty("drafts")[0].GetProperty("id").GetString(), complete = true, words = Array.Empty<object>(), sentences = Array.Empty<object>() }));
            }
            return ("finish_task", "{\"summary\":\"Done\"}");
        };
        var task = await h.Store.StartAsync(h.ThreadId, "user", new("deduplication", new("Create a quiz from these words and sentences")), default);
        await h.Drain();
        Assert.Equal("completed", (await h.Store.ViewAsync(task.Id, "user", default)).Status);
        Assert.Empty(await h.Db.Words.ToListAsync());
        Assert.Equal("Ala ma kota.", (await h.Db.QuizSentences.SingleAsync()).Text);
    }

    [Fact]
    public async Task Deleting_chat_cascades_operational_records_and_prevents_resume()
    {
        await using var h = await Harness.Create();
        var task = await h.Start("Add dom");
        await h.Step(); await h.Step(); await h.Step();
        h.Db.ChangeTracker.Clear();
        await h.Db.AssistantThreads.Where(x => x.Id == h.ThreadId).ExecuteDeleteAsync();
        Assert.Empty(await h.Db.AssistantTasks.ToListAsync());
        Assert.Empty(await h.Db.AssistantTaskCalls.ToListAsync());
        Assert.Null(await h.Store.ClaimAsync(default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Store.ViewAsync(task.Id, "user", default));
    }

    [Fact]
    public void Source_sections_preserve_all_50000_characters_and_stable_ids()
    {
        var source = string.Concat(Enumerable.Repeat("Jarzyna miał urodziny, pojechałem na Młociny\nKac gigant i pizza na tłustym cieście\n", 800))[..50000];
        var sections = RuntimeJson.Split(source);
        Assert.Equal(source, string.Concat(sections.Select(x => x.Text)));
        Assert.Equal(sections, RuntimeJson.Split(source));
        Assert.All(sections, x => Assert.InRange(x.Text.Length, 1, 2400));
    }

    [Fact]
    public async Task Multiple_quiz_batches_save_one_quiz_with_all_words_and_sentences()
    {
        await using var h = await Harness.Create();
        await Multiple_quiz_batches_save_one_quiz_with_all_words_and_sentences_scenario(h);
    }

    [SqlServerFact]
    public Task SqlServer_Multiple_quiz_batches_save_one_quiz_with_all_words_and_sentences() => OnSqlServer("assistant-batches", Multiple_quiz_batches_save_one_quiz_with_all_words_and_sentences_scenario);

    private static async Task Multiple_quiz_batches_save_one_quiz_with_all_words_and_sentences_scenario(Harness h)
    {
        var thread = await h.Db.AssistantThreads.SingleAsync(); thread.ContextQuizId = null;
        await h.Db.SaveChangesAsync();
        h.Model.Script = (request, turn) =>
        {
            if (turn > 2) return ("finish_task", "{\"summary\":\"Created the whole quiz.\"}");
            var state = RuntimeJson.Read<AssistantRuntimeState>(h.Db.AssistantTasks.AsNoTracking().Single().StateJson);
            var draft = state.PendingChanges.FirstOrDefault()?.Payload.GetProperty("draft_id").GetString();
            var start = turn == 1 ? 0 : 100;
            var count = turn == 1 ? 100 : 50;
            return ("create_vocabulary_quiz", RuntimeJson.Write(new
            {
                name = "Full source", draft_id = draft, complete = turn == 2,
                source_language = "English", target_language = "Polish",
                words = Enumerable.Range(start, count).Select(i => new { word = "słowo" + i, translation = "word" + i }),
                sentences = Enumerable.Range(start, count).Select(i => new { text = "To jest zdanie " + i, translation = "This is sentence " + i }),
            }));
        };
        await h.Store.StartAsync(h.ThreadId, "user", new("batch-quiz", new("Create a quiz with all words and sentences")), default);
        await h.Step(); await h.Step(); await h.Step();
        Assert.Equal("Building", (await h.Db.Quizzes.SingleAsync(x => x.Id != h.QuizId)).ProcessingStatus);
        Assert.Equal(100, await h.Db.Words.CountAsync());
        h.Restart();
        await h.Drain();
        Assert.Equal(150, await h.Db.Words.CountAsync());
        Assert.Equal(150, await h.Db.QuizSentences.CountAsync());
        Assert.Equal("Ready", (await h.Db.Quizzes.SingleAsync(x => x.Id != h.QuizId)).ProcessingStatus);
        Assert.Equal(2, await h.Db.Quizzes.CountAsync());
    }

    [Fact]
    public async Task Failure_after_content_save_rolls_back_journal_and_content_together()
    {
        await using var h = await Harness.Create();
        var fail = true;
        h.WrapApplier = inner => new FailAfterSave(inner, () => { var result = fail; fail = false; return result; });
        h.Restart();
        var task = await h.Start("Add dom");
        await h.Step(); await h.Step(); await h.Step();
        Assert.Empty(await h.Db.Words.AsNoTracking().ToListAsync());
        Assert.Equal("pending", (await h.Db.AssistantTaskCalls.AsNoTracking().SingleAsync()).Status);
        var paused = await h.Store.ViewAsync(task.Id, "user", default);
        Assert.Equal("paused", paused.Status);
        h.Db.ChangeTracker.Clear();
        await h.Store.CommandAsync(task.Id, "user", "resume", new(paused.Revision), default);
        await h.Drain();
        Assert.Equal("dom", (await h.Db.Words.SingleAsync()).Lemma);
        Assert.Equal("completed", (await h.Store.ViewAsync(task.Id, "user", default)).Status);
    }

    [Fact]
    public async Task Work_continues_after_sixty_seconds_without_a_browser_request()
    {
        await using var h = await Harness.Create();
        var task = await h.Start("Add dom");
        await h.Step(); await h.Step();
        h.Clock.Advance(TimeSpan.FromSeconds(75));
        h.Restart();
        await h.Drain();
        var reconnect = await h.Store.ActiveAsync(h.ThreadId, "user", default);
        Assert.Equal(task.Id, reconnect!.Id);
        Assert.Equal("completed", reconnect.Status);
        Assert.Single(await h.Db.Words.ToListAsync());
    }

    private sealed class FailAfterSave(IChangeApplier inner, Func<bool> fail) : IChangeApplier
    {
        public async Task<AssistantApplyResult> ApplyAsync(Guid? quizId, string userId, IReadOnlyList<PendingChange> changes, CancellationToken ct)
        {
            var result = await inner.ApplyAsync(quizId, userId, changes, ct);
            if (fail()) throw new IOException("Simulated failure after content write, before task commit.");
            return result;
        }
    }

    [SqlServerFact]
    public async Task SqlServer_competing_workers_claim_only_one_lease()
    {
        await SqlServerTestDatabase.RunAsync("assistant-leases", async db =>
        {
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero));
            var settings = Microsoft.Extensions.Options.Options.Create(new AssistantRuntimeOptions { Enabled = true });
            var thread = new AssistantThread { Id = Guid.NewGuid(), UserId = "worker-owner", Title = "Lease test" };
            db.Users.Add(new ApplicationUser { Id = thread.UserId, UserName = thread.UserId });
            db.AssistantThreads.Add(thread);
            await db.SaveChangesAsync();
            var store = new AssistantTaskStore(db, settings, clock);
            await store.StartAsync(thread.Id, thread.UserId, new("lease", new("Explain a word")), default);
            var dbOptions = new DbContextOptionsBuilder<GlosifyContext>().UseSqlServer(db.Database.GetConnectionString()).Options;
            await using var other = new GlosifyContext(dbOptions);
            var claims = await Task.WhenAll(store.ClaimAsync(default), new AssistantTaskStore(other, settings, clock).ClaimAsync(default));
            Assert.Single(claims, x => x.HasValue);
            Assert.Single(await db.AssistantTasks.ToListAsync());
        });
    }

    [Fact]
    public async Task Quiz_in_public_collection_stays_private_until_completed_move_is_approved()
    {
        await using var h = await Harness.Create();
        var collection = new Collection { Id = Guid.NewGuid(), UserId = "user", Name = "Public", Language = "Polish", IsPublic = true };
        h.Db.Collections.Add(collection);
        (await h.Db.AssistantThreads.SingleAsync()).ContextQuizId = null;
        await h.Db.SaveChangesAsync();
        h.Model.Script = (_, n) => n == 1
            ? ("create_vocabulary_quiz", RuntimeJson.Write(new { name = "New quiz", source_language = "English", target_language = "Polish",
                collection_id = collection.Id, complete = true, words = new[] { new { word = "dom", translation = "house" } } }))
            : ("finish_task", "{\"summary\":\"Finished.\"}");
        var task = await h.Store.StartAsync(h.ThreadId, "user", new("public", new("Create a vocabulary quiz")), default);
        await h.Step(); await h.Step(); await h.Step();
        var quiz = await h.Db.Quizzes.AsNoTracking().SingleAsync(x => x.Id != h.QuizId);
        Assert.Null(quiz.CollectionId);
        Assert.False(quiz.IsPublic);
        await h.Drain();
        var review = await h.Store.ViewAsync(task.Id, "user", default);
        Assert.Equal("awaiting_approval", review.Status);
        Assert.Equal(PendingChangeKinds.MoveQuiz, Assert.Single(review.ApprovalChanges).Kind);
        Assert.Contains("Public", review.ApprovalChanges[0].Summary);
        h.Db.ChangeTracker.Clear();
        await h.Store.CommandAsync(task.Id, "user", "approve", new(review.Revision), default);
        await h.Drain();
        Assert.Equal(collection.Id, (await h.Db.Quizzes.AsNoTracking().SingleAsync(x => x.Id == quiz.Id)).CollectionId);
        Assert.Equal("completed", (await h.Store.ViewAsync(task.Id, "user", default)).Status);
    }

    [Fact]
    public async Task Steering_can_discard_a_deferred_collection_move_without_losing_the_quiz()
    {
        await using var h = await Harness.Create();
        var collection = new Collection { Id = Guid.NewGuid(), UserId = "user", Name = "Public", Language = "Polish", IsPublic = true };
        h.Db.Collections.Add(collection);
        (await h.Db.AssistantThreads.SingleAsync()).ContextQuizId = null;
        await h.Db.SaveChangesAsync();
        h.Model.Script = (_, n) => n == 1
            ? ("create_vocabulary_quiz", RuntimeJson.Write(new { name = "New quiz", source_language = "English", target_language = "Polish",
                collection_id = collection.Id, complete = true, words = new[] { new { word = "dom", translation = "house" } } }))
            : ("finish_task", "{\"summary\":\"Finished privately.\"}");
        var task = await h.Store.StartAsync(h.ThreadId, "user", new("discard-move", new("Create a vocabulary quiz")), default);
        await h.Drain();
        var review = await h.Store.ViewAsync(task.Id, "user", default);
        Assert.Equal("awaiting_approval", review.Status);
        h.Db.ChangeTracker.Clear();
        await h.Store.CommandAsync(task.Id, "user", "steer", new(review.Revision, "Keep the quiz private outside this collection."), default);
        await h.Drain();
        Assert.Equal("completed", (await h.Store.ViewAsync(task.Id, "user", default)).Status);
        var quiz = await h.Db.Quizzes.AsNoTracking().SingleAsync(x => x.Id != h.QuizId);
        Assert.Null(quiz.CollectionId);
        Assert.Equal("Ready", quiz.ProcessingStatus);
        Assert.Single(await h.Db.Words.ToListAsync());
    }

    [Fact]
    public async Task Coverage_rejects_early_finish_and_preserves_late_content_with_repeated_sections()
    {
        await using var h = await Harness.Create();
        (await h.Db.AssistantThreads.SingleAsync()).ContextQuizId = null;
        await h.Db.SaveChangesAsync();
        const string refrain = "Kac gigant i pizza na tłustym cieście";
        const string ending = "Ostatnia stacja Rondo Daszyńskiego";
        var text = string.Concat(Enumerable.Repeat(refrain + "\n", 140)) + ending;
        var sections = RuntimeJson.Split(text);
        var stage = 0;
        var sectionIndex = 0;
        h.Model.Script = (_, turn) =>
        {
            if (turn == 1 || sectionIndex == sections.Count) return ("finish_task", "{\"summary\":\"All sections processed.\"}");
            var section = sections[sectionIndex];
            var state = RuntimeJson.Read<AssistantRuntimeState>(h.Db.AssistantTasks.AsNoTracking().Single().StateJson);
            if (stage++ == 0) return ("read_source_section", RuntimeJson.Write(new { section_id = section.Id }));
            if (stage == 2)
            {
                var draft = state.PendingChanges.FirstOrDefault()?.Payload.GetProperty("draft_id").GetString();
                return ("create_vocabulary_quiz", RuntimeJson.Write(new { name = "Full source", draft_id = draft,
                    complete = sectionIndex == sections.Count - 1, source_language = "English", target_language = "Polish",
                    sentences = section.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Distinct()
                        .Select(x => new { text = x, translation = "Unscored test value" }) }));
            }
            stage = 0;
            sectionIndex++;
            var savedCall = h.Db.AssistantTaskCalls.AsNoTracking().Where(x => x.TaskId == h.Db.AssistantTasks.Single().Id && x.Status == "success" && x.ToolName == "create_vocabulary_quiz")
                .OrderBy(x => x.Sequence).ToList().LastOrDefault(x => RuntimeJson.Read<AssistantToolOutcome>(x.ResultJson!).Saved > 0);
            return ("complete_source_section", RuntimeJson.Write(new { section_id = section.Id,
                coverage_json = RuntimeJson.Write(new[] { new { start = 0, length = section.Text.Length,
                    saved_call = savedCall?.Sequence, exclusion_reason = (string?)null } }) }));
        };
        var task = await h.Store.StartAsync(h.ThreadId, "user", new("coverage", new(text)), default);
        await h.Step(); await h.Step(); await h.Step();
        Assert.Equal("correctable", (await h.Db.AssistantTaskCalls.SingleAsync()).Status);
        Assert.Empty(await h.Db.QuizSentences.ToListAsync());
        await h.Drain(60);
        var view = await h.Store.ViewAsync(task.Id, "user", default);
        for (var window = 0; view.Status == "paused" && window < 3; window++)
        {
            Assert.Contains("budget", view.Reason, StringComparison.OrdinalIgnoreCase);
            h.Db.ChangeTracker.Clear();
            await h.Store.CommandAsync(task.Id, "user", "resume", new(view.Revision), default);
            h.Restart();
            await h.Drain(60);
            view = await h.Store.ViewAsync(task.Id, "user", default);
        }
        Assert.True(view.Status == "completed", RuntimeJson.Write(view));
        var sentences = await h.Db.QuizSentences.AsNoTracking().ToListAsync();
        Assert.Equal(2, sentences.Count);
        Assert.Contains(sentences, x => x.Text == ending);
        Assert.Contains(sentences, x => x.Text == refrain);
        var completed = RuntimeJson.Read<AssistantRuntimeState>((await h.Db.AssistantTasks.AsNoTracking().SingleAsync()).StateJson);
        Assert.Equal(sections.Count, completed.CoveredSections.Count);
        Assert.Equal("Ready", Assert.Single(view.Artifacts).Status);
    }

    [Theory]
    [InlineData("evaluated")]
    [InlineData("unavailable")]
    [InlineData("throw")]
    [InlineData("budget")]
    public async Task Advisory_evaluation_never_changes_completed_work_or_reconstructs_the_decision(string outcome)
    {
        await using var h = await Harness.Create();
        await Advisory_evaluation_never_changes_completed_work_or_reconstructs_the_decision_scenario(h, outcome);
    }

    [SqlServerFact]
    public Task SqlServer_Advisory_evaluation_never_changes_completed_work_or_reconstructs_the_decision() =>
        OnSqlServer("assistant-evaluation", h => Advisory_evaluation_never_changes_completed_work_or_reconstructs_the_decision_scenario(h, "evaluated"));

    private static async Task Advisory_evaluation_never_changes_completed_work_or_reconstructs_the_decision_scenario(Harness h, string outcome)
    {
        var task = await h.Start("Add dom");
        await h.Drain();
        var call = await h.Db.AssistantTaskCalls.AsNoTracking().SingleAsync(x => x.ToolName == "add_word");
        await h.Db.AssistantTaskCalls.Where(x => x.Id == call.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.EvaluationStatus, "pending"));
        await h.Db.Words.ExecuteUpdateAsync(s => s.SetProperty(x => x.Lemma, "user's later edit"));
        if (outcome == "budget") await h.Db.AssistantTasks.ExecuteUpdateAsync(s => s.SetProperty(x => x.Tokens, h.Options.MaxTokens));
        h.Db.ChangeTracker.Clear();
        var evaluator = new RecordingEvaluator(outcome);
        var credits = new EvaluationCredits();
        var worker = new AssistantTaskEvaluationWorker(h.Db, h.Store, evaluator,
            Microsoft.Extensions.Options.Options.Create(new JevOptions { Enabled = true }),
            Microsoft.Extensions.Options.Options.Create(h.Options), credits,
            Microsoft.Extensions.Options.Options.Create(new AiUsageOptions { MonthlyBudget = new() { Enabled = false } }));
        await worker.EvaluateOneAsync(default);
        var result = await h.Store.ViewAsync(task.Id, "user", default);
        Assert.Equal("completed", result.Status);
        Assert.Equal(1, result.SavedChanges);
        Assert.Equal("user's later edit", (await h.Db.Words.AsNoTracking().SingleAsync()).Lemma);
        var reviewed = await h.Db.AssistantTaskCalls.AsNoTracking().SingleAsync(x => x.Id == call.Id);
        Assert.Equal(call.SnapshotHash, reviewed.SnapshotHash);
        if (outcome == "budget")
        {
            Assert.Null(evaluator.Snapshot);
            Assert.Equal("pending_budget", result.Activity.Single(x => x.Id == call.Id).EvaluationStatus);
            Assert.Equal(0, credits.Reservations);
        }
        else
        {
            Assert.Equal("dom", evaluator.Snapshot!.Arguments.GetProperty("word").GetString());
            Assert.Contains("success", evaluator.Snapshot.ExecutionResult);
            Assert.Equal(outcome == "evaluated" ? "evaluated" : "unavailable", reviewed.EvaluationStatus);
            Assert.Equal(outcome == "evaluated" ? 1 : 0, result.EvaluatedCalls);
            Assert.Equal(1, credits.Reservations);
            Assert.Equal(outcome == "evaluated" ? 0 : 1, credits.Releases);
        }
    }

    private sealed class RecordingEvaluator(string outcome) : IToolUseEvaluator
    {
        internal ToolDecisionSnapshot? Snapshot { get; private set; }
        public Task<ToolUseEvaluation> EvaluateAsync(ToolDecisionSnapshot snapshot, CancellationToken cancellationToken)
        {
            Snapshot = snapshot;
            if (outcome == "throw") throw new HttpRequestException("Evaluator is offline");
            return Task.FromResult(new ToolUseEvaluation(outcome, "jev-1.13.0", JevToolUseEvaluator.RubricVersion,
                outcome == "evaluated" ? [new("wrong_content_destination", "violation", .95, .95, ["arguments"], "Template finding")] : [], 100));
        }
    }
    private sealed class EvaluationCredits : IAiCreditService
    {
        internal int Reservations { get; private set; }
        internal int Releases { get; private set; }
        public Task<AiCreditReservation> ReserveAsync(AiUsageContext context, string provider, string model, int estimatedTokens, CancellationToken cancellationToken = default)
        {
            Assert.Equal("typesafe", provider); Reservations++;
            return Task.FromResult(new AiCreditReservation(Guid.NewGuid(), context.UserId, 1, estimatedTokens));
        }
        public Task CommitUsageAsync(Guid reservationId, AiTokenUsage usage, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken = default) { Releases++; return Task.CompletedTask; }
        public Task<AiCreditAccountView> GetOrCreateAccountAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AiCreditTransaction>> GetRecentTransactionsAsync(string userId, int count = 25, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task GrantAsync(string adminUserId, string targetUserId, int credits, string note, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Repeated_successful_reads_without_new_information_require_a_changed_approach_then_pause()
    {
        await using var h = await Harness.Create();
        h.Model.ReportedTokens = 1000;
        var requestedNewApproach = false;
        h.Model.Script = (request, _) =>
        {
            requestedNewApproach |= request.SystemInstruction.Contains("Change approach");
            return ("get_quiz_summary", "{}");
        };
        var task = await h.Start("Explain this quiz");
        await h.Drain(30);
        var result = await h.Store.ViewAsync(task.Id, "user", default);
        Assert.Equal("paused", result.Status);
        Assert.Contains("no progress", result.Reason);
        Assert.True(requestedNewApproach);
        Assert.Equal(7, h.Model.Calls);
        Assert.Empty(await h.Db.Words.ToListAsync());
    }

    [Fact]
    public async Task Long_retry_after_pauses_the_window_and_resume_does_not_retry_early()
    {
        await using var h = await Harness.Create();
        h.Model.Failure = new GenerativeAiDependencyUnavailableException("Rate limited",
            new OpenAiTransportException(429, "Rate limited") { RetryAfter = TimeSpan.FromMinutes(10) });
        var task = await h.Start("Add dom");
        await h.Step(); await h.Step();
        var paused = await h.Store.ViewAsync(task.Id, "user", default);
        Assert.Equal("paused", paused.Status);
        Assert.Contains("provider requested waiting", paused.Reason);
        h.Db.ChangeTracker.Clear();
        await h.Store.CommandAsync(task.Id, "user", "resume", new(paused.Revision), default);
        Assert.Null(await h.Store.ClaimAsync(default));
        Assert.Equal(1, h.Model.Calls);
        h.Clock.Advance(TimeSpan.FromMinutes(11));
        await h.Step(); // The waiting window expired; no request goes to the provider.
        Assert.Equal(1, h.Model.Calls);
        var ready = await h.Store.ViewAsync(task.Id, "user", default);
        Assert.Equal("paused", ready.Status);
        h.Model.Failure = null;
        h.Db.ChangeTracker.Clear();
        await h.Store.CommandAsync(task.Id, "user", "resume", new(ready.Revision), default);
        Assert.NotNull(await h.Store.ClaimAsync(default));
    }

    [Fact]
    public async Task Earlier_chat_replays_text_replies_without_orphaned_tool_results()
    {
        await using var h = await Harness.Create();
        AssistantMessage Message(int sequence, string role, object content) => new()
        {
            Id = Guid.NewGuid(), ThreadId = h.ThreadId, Sequence = sequence, Role = role,
            ContentJson = RuntimeJson.Write(content), CreatedAt = h.Clock.GetUtcNow(),
        };
        // A legacy turn: question, tool call, tool result (stored as a user message), answer.
        h.Db.AssistantMessages.AddRange(
            Message(0, "user", new { parts = new[] { new { kind = "text", text = "What does dom mean?" } } }),
            Message(1, "model", new { parts = new[] { new { kind = "function_call", name = "get_quiz_summary", argsJson = "{}", callId = "old-call" } } }),
            Message(2, "user", new { parts = new[] { new { kind = "function_response", name = "get_quiz_summary", callId = "old-call", responseJson = "{}" } } }),
            Message(3, "model", new { parts = new[] { new { kind = "text", text = "Dom means house." } } }));
        await h.Db.SaveChangesAsync();
        await h.Start("Add that word");
        await h.Step(); // initialize
        await h.Step(); // model call
        var history = h.Model.Requests[0].History;
        Assert.Equal(3, history.Count);
        Assert.Contains("What does dom mean?", history[0].ContentJson);
        Assert.Equal("model", history[1].Role);
        Assert.Contains("Dom means house.", history[1].ContentJson);
        Assert.Contains("Add that word", history[2].ContentJson);
        Assert.DoesNotContain(history, x => x.ContentJson.Contains("function_"));
        var state = RuntimeJson.Read<AssistantRuntimeState>((await h.Db.AssistantTasks.AsNoTracking().SingleAsync()).StateJson);
        Assert.Equal(2, state.RequestIndex);
    }

    [Fact]
    public async Task Synchronous_task_question_ends_the_turn_and_frees_the_user()
    {
        await using var h = await Harness.Create();
        h.Model.Script = (_, n) => n == 1 ? ("ask_user", "{\"question\":\"Which quiz should I use?\"}") : ("finish_task", "{\"summary\":\"Done.\"}");
        var task = await h.Store.StartAsync(h.ThreadId, "user", new("sync-question", new("Add a word", h.QuizId)), default, manualApproval: true);
        await h.Drain();
        var view = await h.Store.ViewAsync(task.Id, "user", default);
        Assert.Equal("completed", view.Status);
        var response = RuntimeJson.Read<AssistantTurnResponse>(RuntimeJson.Write(view.Result));
        Assert.Equal("Which quiz should I use?", response.AssistantText);
        Assert.Null((await h.Db.AssistantTasks.AsNoTracking().SingleAsync()).ActiveUserId);

        // The answer is a new task, and it sees the question it answers.
        h.Db.ChangeTracker.Clear();
        await h.Store.StartAsync(h.ThreadId, "user", new("sync-answer", new("The Polish one", h.QuizId)), default, manualApproval: true);
        await h.Step(); await h.Step();
        var replay = h.Model.Requests.Last().History;
        Assert.Contains(replay, x => x.Role == "model" && x.ContentJson.Contains("Which quiz should I use?"));
    }

    [Fact]
    public async Task Synchronous_task_that_would_pause_ends_with_its_partial_proposals()
    {
        await using var h = await Harness.Create();
        h.Options.MaxModelCalls = 1;
        var task = await h.Store.StartAsync(h.ThreadId, "user", new("sync-budget", new("Add dom and more", h.QuizId)), default, manualApproval: true);
        await h.Drain();
        var view = await h.Store.ViewAsync(task.Id, "user", default);
        Assert.Equal("failed", view.Status);
        Assert.Contains("budget", view.Reason);
        var response = RuntimeJson.Read<AssistantTurnResponse>(RuntimeJson.Write(view.Result));
        Assert.Contains("could not finish", response.AssistantText);
        Assert.Contains("partial", response.AssistantText);
        Assert.DoesNotContain("Resume", response.AssistantText);
        Assert.Equal(PendingChangeKinds.AddWord, Assert.Single(response.PendingChanges).Kind);
        Assert.Empty(await h.Db.Words.ToListAsync()); // Still a proposal for Apply.
        Assert.Null((await h.Db.AssistantTasks.AsNoTracking().SingleAsync()).ActiveUserId);
        h.Db.ChangeTracker.Clear();
        await h.Store.StartAsync(h.ThreadId, "user", new("sync-next", new("Try again", h.QuizId)), default, manualApproval: true);
    }

    [Fact]
    public async Task Stop_and_steering_apply_after_the_worker_advances_the_revision()
    {
        await using var h = await Harness.Create();
        var task = await h.Start("Add dom");
        await h.Step(); await h.Step();
        Assert.True((await h.Store.ViewAsync(task.Id, "user", default)).Revision > task.Revision);
        h.Db.ChangeTracker.Clear();
        await h.Store.CommandAsync(task.Id, "user", "steer", new(task.Revision, "Also add kot."), default);
        h.Db.ChangeTracker.Clear();
        var stopped = await h.Store.CommandAsync(task.Id, "user", "cancel", new(task.Revision), default);
        Assert.Equal("cancelled", stopped.Status);
        Assert.Equal(new[] { "Also add kot." }, RuntimeJson.Read<List<string>>((await h.Db.AssistantTasks.AsNoTracking().SingleAsync()).SteeringJson));
    }

    [Fact]
    public async Task Stop_retries_a_worker_checkpoint_race_but_approval_does_not()
    {
        await using var h = await Harness.Create();
        await Stop_retries_a_worker_checkpoint_race_but_approval_does_not_scenario(h);
    }

    [SqlServerFact]
    public Task SqlServer_Stop_retries_a_worker_checkpoint_race_but_approval_does_not() => OnSqlServer("assistant-stop-race", Stop_retries_a_worker_checkpoint_race_but_approval_does_not_scenario);

    private static async Task Stop_retries_a_worker_checkpoint_race_but_approval_does_not_scenario(Harness h)
    {
        h.Db.Words.Add(new Word { Id = "word", QuizId = h.QuizId, Lemma = "dom", Translation = "house" });
        await h.Db.SaveChangesAsync();
        h.Model.FirstName = "delete_word";
        h.Model.FirstArgs = "{\"word_id\":\"word\"}";
        var task = await h.Start("Delete the word dom");
        await h.Step(); await h.Step(); await h.Step();
        var review = await h.Store.ViewAsync(task.Id, "user", default);
        Assert.Equal("awaiting_approval", review.Status);

        // The worker commits a checkpoint between the command's read and its write.
        var race = new BumpRevisionOnce();
        await using (var raced = h.CreateContext(race))
        {
            var store = new AssistantTaskStore(raced, Microsoft.Extensions.Options.Options.Create(h.Options), h.Clock);
            await Assert.ThrowsAsync<AssistantTaskConflictException>(() => store.CommandAsync(task.Id, "user", "approve", new(review.Revision), default));
            Assert.Equal(1, race.Fired);
            race.Armed = true;
            raced.ChangeTracker.Clear();
            var stopped = await store.CommandAsync(task.Id, "user", "cancel", new(review.Revision), default);
            Assert.Equal(2, race.Fired);
            Assert.Equal("cancelled", stopped.Status);
        }
        Assert.Single(await h.Db.Words.ToListAsync()); // Approval never applied.
    }

    [Fact]
    public async Task Checkpoint_conflict_hands_the_task_back_without_waiting_for_lease_expiry()
    {
        var race = new BumpRevisionOnce { Armed = false };
        await using var h = await Harness.Create(race);
        await Checkpoint_conflict_hands_the_task_back_without_waiting_for_lease_expiry_scenario(h, race);
    }

    [SqlServerFact]
    public Task SqlServer_Checkpoint_conflict_hands_the_task_back_without_waiting_for_lease_expiry()
    {
        var race = new BumpRevisionOnce { Armed = false };
        return OnSqlServer("assistant-lease-race", h => Checkpoint_conflict_hands_the_task_back_without_waiting_for_lease_expiry_scenario(h, race), race);
    }

    private static async Task Checkpoint_conflict_hands_the_task_back_without_waiting_for_lease_expiry_scenario(Harness h, BumpRevisionOnce race)
    {
        await h.Start("Add dom");
        await h.Step(); // initialize
        race.Armed = true;
        h.Db.ChangeTracker.Clear();
        var claim = await h.Store.ClaimAsync(default);
        await h.Executor.StepAsync(claim!.Value.Id, claim.Value.Lease, default);
        Assert.Equal(1, race.Fired);
        Assert.Equal(0, h.Model.Calls); // The conflict came before the provider request.
        Assert.NotNull(await h.Store.ClaimAsync(default)); // No two-minute stall.
    }

    [Fact]
    public async Task Heartbeat_stops_the_step_after_repeated_renewal_failures_and_never_throws()
    {
        using var failing = new CancellationTokenSource();
        var attempts = 0;
        await AssistantTaskWorker.HeartbeatAsync(Guid.NewGuid(), failing,
            _ => { attempts++; throw new InvalidOperationException("database unavailable"); },
            TimeSpan.FromMilliseconds(5), NullLogger.Instance);
        Assert.True(failing.IsCancellationRequested);
        Assert.Equal(3, attempts);

        using var lost = new CancellationTokenSource();
        await AssistantTaskWorker.HeartbeatAsync(Guid.NewGuid(), lost, _ => Task.FromResult(0), TimeSpan.FromMilliseconds(5), NullLogger.Instance);
        Assert.True(lost.IsCancellationRequested);

        // One transient failure does not stop a step whose lease is still renewed.
        using var recovering = new CancellationTokenSource();
        var renewals = 0;
        var heartbeat = AssistantTaskWorker.HeartbeatAsync(Guid.NewGuid(), recovering, _ =>
        {
            if (++renewals == 1) throw new TimeoutException();
            if (renewals == 4) recovering.Cancel();
            return Task.FromResult(1);
        }, TimeSpan.FromMilliseconds(5), NullLogger.Instance);
        await heartbeat;
        Assert.Equal(4, renewals);
    }

    [Fact]
    public async Task Data_tools_run_outside_the_content_transaction()
    {
        await using var h = await Harness.Create();
        await Data_tools_run_outside_the_content_transaction_scenario(h);
    }

    [SqlServerFact]
    public Task SqlServer_Data_tools_run_outside_the_content_transaction() => OnSqlServer("assistant-tool-scope", Data_tools_run_outside_the_content_transaction_scenario);

    private static async Task Data_tools_run_outside_the_content_transaction_scenario(Harness h)
    {
        RecordingTools? recorder = null;
        h.WrapTools = inner => recorder = new RecordingTools(inner, () => h.Db);
        h.Restart();
        await h.Start("Add the word dom");
        await h.Drain();
        // Run once, before the transaction; the transaction reuses the prepared result.
        Assert.False(Assert.Single(recorder!.Executions, x => x.Tool == "add_word").InTransaction);
        Assert.Equal("dom", (await h.Db.Words.SingleAsync()).Lemma);
    }

    [Fact]
    public async Task User_edit_during_tool_execution_is_not_overwritten()
    {
        await using var h = await Harness.Create();
        await User_edit_during_tool_execution_is_not_overwritten_scenario(h);
    }

    [SqlServerFact]
    public Task SqlServer_User_edit_during_tool_execution_is_not_overwritten() => OnSqlServer("assistant-tool-edit", User_edit_during_tool_execution_is_not_overwritten_scenario);

    private static async Task User_edit_during_tool_execution_is_not_overwritten_scenario(Harness h)
    {
        h.Db.Words.Add(new Word { Id = "word", QuizId = h.QuizId, Lemma = "dom", Translation = "house" });
        await h.Db.SaveChangesAsync();
        h.Model.FirstName = "edit_word";
        h.Model.FirstArgs = "{\"word_id\":\"word\",\"word\":\"domek\",\"translation\":\"cottage\"}";
        h.WrapTools = inner => new RecordingTools(inner, () => h.Db, async name =>
        {
            if (name != "edit_word") return;
            await using var user = h.CreateContext();
            await user.Words.Where(x => x.Id == "word").ExecuteUpdateAsync(s => s.SetProperty(x => x.Translation, "user edit"));
        });
        h.Restart();
        await h.Start("Edit dom to domek");
        await h.Step(); await h.Step(); await h.Step();
        Assert.Equal("user edit", (await h.Db.Words.AsNoTracking().SingleAsync()).Translation);
        Assert.Equal("conflict", (await h.Db.AssistantTaskCalls.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public void Compaction_keeps_the_request_and_removes_whole_exchanges()
    {
        var state = new AssistantRuntimeState { RequestIndex = 2 };
        state.History.Add(RuntimeJson.Text("user", "Earlier question"));
        state.History.Add(RuntimeJson.Text("model", "Earlier answer"));
        state.History.Add(RuntimeJson.Text("user", "Current request"));
        for (var i = 0; i < 8; i++)
        {
            state.History.Add(new("model", RuntimeJson.Write(new { parts = new[] { new { kind = "function_call", name = "get_quiz_summary", argsJson = "{}", callId = "c" + i } } })));
            state.History.Add(new("user", RuntimeJson.Write(new { parts = new[] { new { kind = "function_response", name = "get_quiz_summary", callId = "c" + i, responseJson = new string('x', 5000) } } })));
        }
        AssistantTaskExecutor.Compact(state);
        Assert.Equal(0, state.RequestIndex);
        Assert.Contains("Current request", state.History[0].ContentJson);
        Assert.Equal("model", state.History[1].Role); // No orphaned result after the request.
        Assert.Contains("\"c7\"", state.History[^1].ContentJson); // The latest exchange survives.
        Assert.True(state.History.Count <= 9 || state.History.Sum(x => x.ContentJson.Length) <= 24000);
    }

    [Fact]
    public async Task Compacted_history_is_what_the_checkpoint_keeps()
    {
        await using var h = await Harness.Create();
        var task = await h.Start("Add dom");
        await h.Step(); // initialize
        var row = await h.Db.AssistantTasks.AsNoTracking().SingleAsync();
        var state = RuntimeJson.Read<AssistantRuntimeState>(row.StateJson);
        for (var i = 0; i < 8; i++)
        {
            state.History.Add(new("model", RuntimeJson.Write(new { parts = new[] { new { kind = "function_call", name = "get_quiz_summary", argsJson = "{}", callId = "c" + i } } })));
            state.History.Add(new("user", RuntimeJson.Write(new { parts = new[] { new { kind = "function_response", name = "get_quiz_summary", callId = "c" + i, responseJson = new string('x', 5000) } } })));
        }
        var grown = RuntimeJson.Write(state);
        await h.Db.AssistantTasks.ExecuteUpdateAsync(s => s.SetProperty(x => x.StateJson, grown));
        await h.Step(); // model call
        var saved = RuntimeJson.Read<AssistantRuntimeState>((await h.Db.AssistantTasks.AsNoTracking().SingleAsync()).StateJson);
        Assert.True(saved.History.Count <= 10, $"{saved.History.Count} turns were checkpointed.");
        Assert.Contains("Add dom", saved.History[saved.RequestIndex].ContentJson);
        Assert.Equal(h.Model.Requests[0].History.Count + 1, saved.History.Count);
    }

    [Fact]
    public async Task Saved_count_reflects_the_items_the_quiz_actually_stored()
    {
        await using var h = await Harness.Create();
        await Saved_count_reflects_the_items_the_quiz_actually_stored_scenario(h);
    }

    [SqlServerFact]
    public Task SqlServer_Saved_count_reflects_the_items_the_quiz_actually_stored() => OnSqlServer("assistant-saved-count", Saved_count_reflects_the_items_the_quiz_actually_stored_scenario);

    private static async Task Saved_count_reflects_the_items_the_quiz_actually_stored_scenario(Harness h)
    {
        (await h.Db.AssistantThreads.SingleAsync()).ContextQuizId = null;
        await h.Db.SaveChangesAsync();
        h.WrapApplier = inner => new DropLastCreatedWord(inner);
        h.Restart();
        h.Model.Script = (_, n) =>
        {
            if (n > 2) return ("finish_task", "{\"summary\":\"Done.\"}");
            var state = RuntimeJson.Read<AssistantRuntimeState>(h.Db.AssistantTasks.AsNoTracking().Single().StateJson);
            var draft = state.PendingChanges.FirstOrDefault()?.Payload.GetProperty("draft_id").GetString();
            var words = n == 1 ? new[] { "dom", "las", "woda" } : ["kot"];
            return ("create_vocabulary_quiz", RuntimeJson.Write(new { name = "Nature", draft_id = draft, complete = n == 2,
                source_language = "English", target_language = "Polish", words = words.Select(x => new { word = x, translation = "t-" + x }) }));
        };
        var task = await h.Store.StartAsync(h.ThreadId, "user", new("counts", new("Create a vocabulary quiz")), default);
        await h.Step(); await h.Step(); await h.Step();
        // The applier dropped "woda": one quiz and two words were saved, not three.
        Assert.Equal(3, (await h.Store.ViewAsync(task.Id, "user", default)).SavedChanges);
        await h.Drain();
        // The next batch retries the unsaved word alongside the new one.
        var view = await h.Store.ViewAsync(task.Id, "user", default);
        Assert.Equal("completed", view.Status);
        Assert.Equal(5, view.SavedChanges);
        Assert.Equal(new[] { "dom", "kot", "las", "woda" }, (await h.Db.Words.Select(x => x.Lemma).ToListAsync()).Order());
    }

    private sealed class BumpRevisionOnce : SaveChangesInterceptor
    {
        internal bool Armed { get; set; } = true;
        internal int Fired { get; private set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var context = eventData.Context!;
            if (Armed && context.ChangeTracker.Entries<AssistantTask>().Any(x => x.State == EntityState.Modified))
            {
                Armed = false;
                Fired++;
                await context.Database.ExecuteSqlRawAsync("UPDATE AssistantTasks SET Revision = Revision + 1", cancellationToken);
            }
            return result;
        }
    }

    private sealed class RecordingTools(IAssistantTools inner, Func<GlosifyContext> db, Func<string, Task>? during = null) : IAssistantTools
    {
        internal List<(string Tool, bool InTransaction)> Executions { get; } = [];
        public IReadOnlyList<AgentToolDeclaration> Declarations => inner.Declarations;
        public IReadOnlyList<AgentToolDeclaration> GlobalDeclarations => inner.GlobalDeclarations;
        public IReadOnlyList<AgentToolDeclaration> QuizAssistantDeclarations => inner.QuizAssistantDeclarations;
        public IReadOnlyList<AgentToolDeclaration> LibrarianDeclarations => inner.LibrarianDeclarations;
        public IReadOnlyList<AgentToolDeclaration> FreestyleQuizAssistantDeclarations => inner.FreestyleQuizAssistantDeclarations;
        public IReadOnlyList<AgentToolDeclaration> FreestyleLibrarianDeclarations => inner.FreestyleLibrarianDeclarations;
        public string? ResolveCanonicalName(string name) => inner.ResolveCanonicalName(name);
        public async Task<object> ExecuteAsync(string name, string argsJson, AgentToolContext context, CancellationToken cancellationToken)
        {
            Executions.Add((name, db().Database.CurrentTransaction is not null));
            var result = await inner.ExecuteAsync(name, argsJson, context, cancellationToken);
            if (during is not null) await during(name);
            return result;
        }
    }

    private sealed class DropLastCreatedWord(IChangeApplier inner) : IChangeApplier
    {
        public Task<AssistantApplyResult> ApplyAsync(Guid? quizId, string userId, IReadOnlyList<PendingChange> changes, CancellationToken ct) =>
            inner.ApplyAsync(quizId, userId, changes.Select(change =>
            {
                if (change.Kind != PendingChangeKinds.CreateQuiz) return change;
                var payload = JsonNode.Parse(change.Payload.GetRawText())!.AsObject();
                var words = payload["words"]!.AsArray();
                words.RemoveAt(words.Count - 1);
                return change with { Payload = JsonSerializer.SerializeToElement(payload) };
            }).ToList(), ct);
    }

    /// <summary>
    /// Runs a scenario on SQL Server, whose locking and isolation SQLite does not model, with
    /// the production retrying execution strategy.
    /// </summary>
    private static Task OnSqlServer(string purpose, Func<Harness, Task> scenario, params IInterceptor[] interceptors) =>
        SqlServerTestDatabase.RunAsync(purpose, async db =>
        {
            await using var h = await Harness.CreateSqlServer(db.Database.GetConnectionString()!, interceptors);
            await scenario(h);
        });

    private sealed class Harness : IAsyncDisposable
    {
        private readonly Func<IInterceptor[], DbContextOptions<GlosifyContext>> optionsFor;
        private readonly IAsyncDisposable? ownedConnection;
        private readonly DbContextOptions<GlosifyContext> dbOptions;
        internal GlosifyContext Db { get; private set; }
        internal AssistantTaskStore Store { get; private set; } = null!;
        internal AssistantTaskExecutor Executor { get; private set; } = null!;
        internal Func<IChangeApplier, IChangeApplier>? WrapApplier { get; set; }
        internal Func<IAssistantTools, IAssistantTools>? WrapTools { get; set; }
        internal ScriptModel Model { get; } = new();
        internal FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero));
        internal AssistantRuntimeOptions Options { get; } = new() { Enabled = true, CoverageEnabled = true };
        internal Guid ThreadId { get; } = Guid.NewGuid();
        internal Guid QuizId { get; } = Guid.NewGuid();
        private Harness(Func<IInterceptor[], DbContextOptions<GlosifyContext>> optionsFor, IAsyncDisposable? ownedConnection,
            IInterceptor[] interceptors)
        {
            this.optionsFor = optionsFor;
            this.ownedConnection = ownedConnection;
            dbOptions = optionsFor(interceptors);
            Db = new(dbOptions);
        }
        /// <summary>A second context on the same database, as another request would use.</summary>
        internal GlosifyContext CreateContext(params IInterceptor[] interceptors) => new(optionsFor(interceptors));
        internal static async Task<Harness> Create(params IInterceptor[] interceptors)
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var h = new Harness(extra => new DbContextOptionsBuilder<GlosifyContext>().UseSqlite(connection).AddInterceptors(extra).Options,
                connection, interceptors);
            await h.Db.Database.EnsureCreatedAsync();
            await h.SeedAsync();
            return h;
        }
        /// <summary>A harness on an existing, empty SQL Server schema that the caller owns.</summary>
        internal static async Task<Harness> CreateSqlServer(string connectionString, params IInterceptor[] interceptors)
        {
            var h = new Harness(extra => new DbContextOptionsBuilder<GlosifyContext>()
                .UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()).AddInterceptors(extra).Options, null, interceptors);
            await h.SeedAsync();
            return h;
        }
        private async Task SeedAsync()
        {
            Db.Users.Add(new ApplicationUser { Id = "user", UserName = "user", SelectedQuizLanguageCode = "pl" });
            Db.Quizzes.Add(new Quiz { Id = QuizId, UserId = "user", Name = "Polish", SourceLanguage = "English", TargetLanguage = "Polish", Language = "Polish" });
            Db.AssistantThreads.Add(new AssistantThread { Id = ThreadId, UserId = "user", Title = "New chat", ContextQuizId = QuizId });
            await Db.SaveChangesAsync();
            Restart();
        }
        internal void Restart()
        {
            Db.Dispose(); Db = new(dbOptions);
            Store = new(Db, Microsoft.Extensions.Options.Options.Create(Options), Clock);
            var resolver = new AssistantContextResolver(Db, null!, new StaticLanguage(), new QuizLanguagePreferenceService(Db));
            var presenter = new AssistantMessagePresenter();
            var threads = new AssistantThreadStore(Db, resolver, presenter,
                new AssistantTelemetryDeletionQueue(Db, Clock, Microsoft.Extensions.Options.Options.Create(new AssistantAnalyticsOptions())));
            var tools = AssistantToolFactory.Create(Db);
            tools = WrapTools?.Invoke(tools) ?? tools;
            var anki = new AnkiCollectionService(Db, Clock);
            var applier = new ChangeApplier(Db, new QuizService(Db, null!, anki), new CollectionService(Db, anki), NullLogger<ChangeApplier>.Instance, anki);
            Executor = new(Db, Store, new AssistantRuntimeContext(Db, resolver, new AssistantPromptBuilder(), tools, new AssistantIntentResolver()),
                Model, tools, WrapApplier?.Invoke(applier) ?? applier, threads, presenter, Microsoft.Extensions.Options.Options.Create(Options),
                Microsoft.Extensions.Options.Options.Create(new JevOptions()), NullLogger<AssistantTaskExecutor>.Instance);
        }
        internal Task<AssistantTaskView> Start(string message) => Store.StartAsync(ThreadId, "user", new(Guid.NewGuid().ToString(), new(message, QuizId)), default);
        internal async Task Step()
        {
            Db.ChangeTracker.Clear();
            var claim = await Store.ClaimAsync(default);
            Assert.NotNull(claim);
            await Executor.StepAsync(claim.Value.Id, claim.Value.Lease, default);
        }
        internal async Task Drain(int maxSteps = 12)
        {
            for (var i = 0; i < maxSteps; i++)
            {
                Db.ChangeTracker.Clear();
                var claim = await Store.ClaimAsync(default);
                if (claim is null) return;
                await Executor.StepAsync(claim.Value.Id, claim.Value.Lease, default);
            }
            Assert.Fail("Task did not settle within the expected steps.");
        }
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            if (ownedConnection is not null) await ownedConnection.DisposeAsync();
        }
    }
    private sealed class ScriptModel : IGenerativeAiClient
    {
        public Func<AgentRequest, int, (string Name, string Args)>? Script { get; set; }
        /// <summary>Several calls in one model turn; null falls back to <see cref="Script"/>.</summary>
        public Func<int, IReadOnlyList<(string Name, string Args)>?>? Batch { get; set; }
        public int Calls { get; private set; }
        public List<AgentRequest> Requests { get; } = [];
        public Exception? Failure { get; set; }
        public int? ReportedTokens { get; set; }
        public string FirstName { get; set; } = "add_word";
        public string FirstArgs { get; set; } = "{\"word\":\"dom\",\"translation\":\"house\"}";
        public Task<AgentTurnResult> RunAgentTurnAsync(AgentRequest request, AiUsageContext usage, CancellationToken cancellationToken = default)
        {
            Calls++;
            Requests.Add(request);
            if (Failure is not null) throw Failure;
            if (Batch?.Invoke(Calls) is { } batch)
                return Task.FromResult(new AgentTurnResult("", batch.Select((x, i) => new AgentFunctionCall(x.Name, x.Args) { CallId = $"call-{Calls}-{i}" }).ToList()));
            var scripted = Script?.Invoke(request, Calls);
            // A scripted name of "text" is a prose reply without tool calls.
            if (scripted?.Name == "text") return Task.FromResult(new AgentTurnResult(scripted.Value.Args, []));
            var name = scripted?.Name ?? (Calls == 1 ? FirstName : "finish_task");
            var args = JsonNode.Parse(scripted?.Args ?? (Calls == 1 ? FirstArgs : "{\"summary\":\"Finished.\"}"))!.AsObject();
            var schema = JsonSerializer.SerializeToElement(request.Tools.Single(x => x.Name == name).ParametersJsonSchema);
            foreach (var field in schema.GetProperty("required").EnumerateArray())
                if (!args.ContainsKey(field.GetString()!)) args[field.GetString()!] = null;
            return Task.FromResult(new AgentTurnResult("", [new(name, args.ToJsonString()) { CallId = "call-" + Calls }])
            {
                Metadata = ReportedTokens is int tokens ? new("test", "test", null, new(tokens, 0, 0, 0, tokens)) : null,
            });
        }
        public Task<T> GenerateStructuredAsync<T>(string prompt, AiUsageContext context, string? model = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ExtractTextFromImageAsync(byte[] image, string contentType, string prompt, AiUsageContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class StaticLanguage : ILanguageContext
    {
        public string? CurrentLanguage => "Polish";
        public IReadOnlyList<string> SupportedLanguages => ["Polish"];
        public bool TrySetLanguage(string language) => true;
        public void Clear() { }
    }
}
