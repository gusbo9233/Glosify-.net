using System.Text.Json;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Assistant.Runtime;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantRunTests
{
    private static readonly object AddDom = new { quiz_id = (string?)null, words = new[] { new { word = "dom", translation = "house" } }, sentences = (object?)null };

    [Fact]
    public async Task A_question_is_answered_in_one_model_call()
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Model.ThenText("Dom means house.");

        var run = await h.RunAsync("What does dom mean?");

        Assert.Equal(AssistantRunStatus.Completed, run.Status);
        Assert.Equal(1, h.Model.Calls);
        Assert.Equal("Dom means house.", Assert.Single(run.Parts).Text);
        await using var db = h.Db();
        var reply = await db.AssistantMessages.SingleAsync(message => message.Id == run.MessageId);
        Assert.Contains("Dom means house.", reply.ContentJson);
        var turn = await db.AssistantTurns.SingleAsync(candidate => candidate.Id == run.TurnId);
        Assert.Equal(AssistantTurnStatus.Completed, turn.Status);
        Assert.Equal(run.MessageId, turn.FinalMessageId);
        Assert.Null((await db.AssistantRuns.SingleAsync()).ActiveUserId);
        // Raw provider items only replay within their run, so they are not kept after it.
        Assert.False(await db.AssistantParts.AnyAsync(part => part.Type == AssistantPartTypes.Step));
    }

    [Fact]
    public async Task Additions_are_saved_immediately_and_journaled_for_undo()
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Model.ThenCall("add_items", AddDom).ThenText("Added dom.");

        var run = await h.RunAsync("Add dom");

        Assert.Equal(AssistantRunStatus.Completed, run.Status);
        Assert.Equal(1, run.SavedChanges);
        Assert.True(run.CanUndo);
        await using var db = h.Db();
        var word = await db.Words.SingleAsync();
        Assert.Equal("dom", word.Lemma);
        var change = await db.AssistantChanges.SingleAsync();
        Assert.Equal(PendingChangeKinds.AddWord, change.Kind);
        Assert.Equal(word.Id, change.EntityId);
        var tool = Assert.Single(run.Parts, part => part.Type == AssistantPartTypes.Tool);
        Assert.Equal(AssistantToolStates.Completed, tool.State);
        Assert.Equal("Add 1 word to “Polish basics”", tool.Title);
        var output = JsonDocument.Parse(h.Model.Requests[1].History.Last().ContentJson).RootElement
            .GetProperty("parts")[0].GetProperty("responseJson").GetString()!;
        Assert.Equal(1, JsonDocument.Parse(output).RootElement.GetProperty("words_added").GetInt32());
    }

    [Fact]
    public async Task A_checkpointed_call_survives_a_restart_and_commits_once()
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Model.ThenCall("add_items", AddDom).ThenText("Added.");
        var run = await h.StartAsync("Add dom");
        await h.StepAsync(); // initialize
        await h.StepAsync(); // model reply with the call, checkpointed
        await using (var db = h.Db())
        {
            Assert.Empty(await db.Words.ToListAsync());
        }

        h.Restart();
        await h.StepAsync(); // saved
        h.Restart();
        await h.DrainAsync();

        await using var after = h.Db();
        Assert.Single(await after.Words.ToListAsync());
        Assert.Equal(2, h.Model.Calls);
        Assert.Equal(AssistantRunStatus.Completed, (await h.ViewAsync(run.Id)).Status);
    }

    [Fact]
    public async Task A_stale_worker_cannot_commit_after_its_lease_is_reassigned()
    {
        await using var h = await AssistantHarness.CreateAsync();
        var run = await h.StartAsync("Hello");
        var (first, second) = await h.WithAsync(async services =>
        {
            var store = services.GetRequiredService<AssistantRunStore>();
            var a = await store.ClaimAsync(default);
            h.Clock.Advance(TimeSpan.FromMinutes(3));
            var b = await store.ClaimAsync(default);
            return (a!.Value, b!.Value);
        });

        await h.WithAsync(async services =>
        {
            await services.GetRequiredService<AssistantRunExecutor>().StepAsync(run.Id, first.Lease, default);
            return 0;
        });
        await using (var db = h.Db())
        {
            Assert.Single(await db.AssistantMessages.ToListAsync());
        }

        await h.WithAsync(async services =>
        {
            await services.GetRequiredService<AssistantRunExecutor>().StepAsync(run.Id, second.Lease, default);
            return 0;
        });
        await using var after = h.Db();
        Assert.Equal(2, await after.AssistantMessages.CountAsync());
    }

    [Fact]
    public async Task The_system_prompt_and_tool_list_stay_identical_across_steps()
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Model.ThenCall("list_items", new { kind = "words", quiz_id = (string?)null, offset = (int?)null })
            .ThenCall("add_items", AddDom)
            .ThenText("Done.");

        await h.RunAsync("Add dom if it is missing");

        Assert.Equal(3, h.Model.Calls);
        var first = h.Model.Requests[0];
        Assert.All(h.Model.Requests, request =>
        {
            Assert.Equal(first.SystemInstruction, request.SystemInstruction);
            Assert.Null(request.ContextInstruction);
            Assert.Null(request.AllowedToolNames);
            Assert.Equal(JsonSerializer.Serialize(first.Tools), JsonSerializer.Serialize(request.Tools));
        });
        // Every earlier request is a strict prefix of the next one, so the provider can reuse
        // the cached prompt for all of it.
        for (var index = 1; index < h.Model.Requests.Count; index++)
        {
            var previous = h.Model.Requests[index - 1].History;
            var current = h.Model.Requests[index].History;
            Assert.Equal(previous.Select(turn => turn.ContentJson), current.Take(previous.Count).Select(turn => turn.ContentJson));
        }

        Assert.DoesNotContain("Polish basics", first.SystemInstruction);
    }

    [Fact]
    public async Task App_context_travels_with_the_user_message_not_the_system_prompt()
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Model.ThenText("Hi!");

        await h.RunAsync("Hello");

        var history = h.Model.Requests[0].History;
        Assert.Equal("developer", history[0].Role);
        Assert.Contains("Selected quiz: \"Polish basics\"", TextOf(history[0]));
        Assert.Contains("Learning language: Polish", TextOf(history[0]));
        Assert.Contains("Translation language: English", TextOf(history[0]));
        Assert.Equal("user", history[1].Role);
        Assert.Equal("Hello", TextOf(history[1]));
        Assert.Null(h.Model.Requests[0].TrailingInstruction);
    }

    [Fact]
    public async Task Earlier_turns_replay_with_their_tool_calls_and_results()
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Model.ThenCall("add_items", AddDom).ThenText("Added dom.").ThenText("You added dom.");
        await h.RunAsync("Add dom");

        await h.RunAsync("What did you add?");

        var history = h.Model.Requests[2].History;
        var json = string.Join("\n", history.Select(turn => turn.ContentJson));
        Assert.Contains("function_call", json);
        Assert.Contains("function_response", json);
        Assert.Contains("Added dom.", json);
        // The earlier run is rebuilt from parts: its encrypted reasoning is not replayed.
        Assert.DoesNotContain("outputItemsJson\":[\"{\\u0022type\\u0022:\\u0022reasoning", history[2].ContentJson);
    }

    internal static string TextOf(AgentTurn turn) =>
        string.Join("\n", JsonDocument.Parse(turn.ContentJson).RootElement.GetProperty("parts").EnumerateArray()
            .Where(part => part.GetProperty("kind").GetString() == "text")
            .Select(part => part.GetProperty("text").GetString()));

    [Fact]
    public async Task Deletions_wait_for_approval_and_apply_only_when_approved()
    {
        await using var h = await AssistantHarness.CreateAsync(db => db.Words.Add(new Word { Id = "w1", QuizId = Guid.Empty, Lemma = "dom", Translation = "house" }));
        h.Model.ThenCall("delete_items", new { quiz_id = (string?)null, word_ids = new[] { "w1" }, sentence_ids = (string[]?)null }).ThenText("Removed dom.");
        var run = await h.StartAsync("Remove dom");
        await h.DrainAsync();

        var waiting = await h.ViewAsync(run.Id);
        Assert.Equal(AssistantRunStatus.AwaitingApproval, waiting.Status);
        Assert.Equal("Remove dom -> house", Assert.Single(waiting.Approval!.Changes).Summary);
        await using (var db = h.Db())
        {
            Assert.Single(await db.Words.ToListAsync());
        }

        await h.CommandAsync(run.Id, "approve");
        await h.DrainAsync();

        Assert.Equal(AssistantRunStatus.Completed, (await h.ViewAsync(run.Id)).Status);
        await using var after = h.Db();
        Assert.Empty(await after.Words.ToListAsync());
        Assert.Equal(PendingChangeKinds.DeleteWord, (await after.AssistantChanges.SingleAsync()).Kind);
    }

    [Fact]
    public async Task A_declined_change_is_reported_back_to_the_model_with_the_reason()
    {
        await using var h = await AssistantHarness.CreateAsync(db => db.Words.Add(new Word { Id = "w1", QuizId = Guid.Empty, Lemma = "dom", Translation = "house" }));
        h.Model.ThenCall("delete_items", new { quiz_id = (string?)null, word_ids = new[] { "w1" }, sentence_ids = (string[]?)null }).ThenText("Kept it.");
        var run = await h.StartAsync("Remove dom");
        await h.DrainAsync();

        await h.CommandAsync(run.Id, "reject", "I still need it");
        await h.DrainAsync();

        Assert.Equal(AssistantRunStatus.Completed, (await h.ViewAsync(run.Id)).Status);
        await using var db = h.Db();
        Assert.Single(await db.Words.ToListAsync());
        var result = AssistantHarness.Transcript(h.Model.Requests[1]);
        Assert.Contains("declined", result);
        Assert.Contains("I still need it", result);
    }

    [Fact]
    public async Task Always_allowing_a_kind_skips_its_approval_for_the_rest_of_the_chat()
    {
        await using var h = await AssistantHarness.CreateAsync(db =>
        {
            db.Words.Add(new Word { Id = "w1", QuizId = Guid.Empty, Lemma = "dom", Translation = "house" });
            db.Words.Add(new Word { Id = "w2", QuizId = Guid.Empty, Lemma = "kot", Translation = "cat" });
        });
        h.Model.ThenCall("delete_items", new { quiz_id = (string?)null, word_ids = new[] { "w1" }, sentence_ids = (string[]?)null }).ThenText("Removed.")
            .ThenCall("delete_items", new { quiz_id = (string?)null, word_ids = new[] { "w2" }, sentence_ids = (string[]?)null }).ThenText("Removed again.");
        var first = await h.StartAsync("Remove dom");
        await h.DrainAsync();
        await h.CommandAsync(first.Id, "approve", always: true);
        await h.DrainAsync();

        var second = await h.RunAsync("Remove kot");

        Assert.Equal(AssistantRunStatus.Completed, second.Status);
        await using var db = h.Db();
        Assert.Empty(await db.Words.ToListAsync());
        Assert.Contains(PendingChangeKinds.DeleteWord, (await db.AssistantThreads.SingleAsync()).ApprovalRules);
    }

    [Fact]
    public async Task A_question_waits_for_the_answer_and_continues_the_same_run()
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Model.ThenCall("ask_user", new { question = "Which topic?", options = new[] { "Food", "Travel" }, multiple = (bool?)null })
            .ThenText("Travel it is.");
        var run = await h.StartAsync("Make me a quiz");
        await h.DrainAsync();

        var waiting = await h.ViewAsync(run.Id);
        Assert.Equal(AssistantRunStatus.AwaitingInput, waiting.Status);
        Assert.Equal(["Food", "Travel"], waiting.Question!.Options);

        await h.WithAsync(services => services.GetRequiredService<AssistantRunStore>().CommandAsync(
            run.Id, AssistantHarness.UserId, "answer", new AssistantRunCommand(waiting.Revision, Answers: ["Travel"]), default));
        await h.DrainAsync();

        Assert.Equal(AssistantRunStatus.Completed, (await h.ViewAsync(run.Id)).Status);
        Assert.Contains("\"answer\":\"Travel\"", AssistantHarness.Transcript(h.Model.Requests[1]));
    }

    [Fact]
    public async Task Steering_supersedes_calls_not_yet_made_and_continues_in_a_new_reply()
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Model.ThenCall("add_items", AddDom).ThenText("Okay, nothing added.");
        var run = await h.StartAsync("Add dom");
        await h.StepAsync();
        await h.StepAsync(); // the add call is checkpointed but not made

        await h.CommandAsync(run.Id, "steer", "Actually, don't add anything.");
        await h.DrainAsync();

        var view = await h.ViewAsync(run.Id);
        Assert.Equal(AssistantRunStatus.Completed, view.Status);
        Assert.Equal(AssistantToolStates.Superseded, view.Parts.Single(part => part.Type == AssistantPartTypes.Tool).State);
        await using var db = h.Db();
        Assert.Empty(await db.Words.ToListAsync());
        var roles = await db.AssistantMessages.OrderBy(message => message.Sequence).Select(message => message.Role).ToListAsync();
        Assert.Equal(["user", "model", "user", "model"], roles);
        Assert.Contains("Actually, don't add anything.", AssistantHarness.Transcript(h.Model.Requests[1]));
    }

    [Fact]
    public async Task Stop_keeps_saved_changes_and_ends_the_run()
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Model.ThenCall("add_items", AddDom).ThenText("unused");
        var run = await h.StartAsync("Add dom");
        await h.StepAsync();
        await h.StepAsync();
        await h.StepAsync(); // saved

        await h.CommandAsync(run.Id, "cancel");

        Assert.False(await h.StepAsync());
        var view = await h.ViewAsync(run.Id);
        Assert.Equal(AssistantRunStatus.Cancelled, view.Status);
        Assert.True(view.CanUndo);
        await using var db = h.Db();
        Assert.Single(await db.Words.ToListAsync());
        Assert.Null((await db.AssistantRuns.SingleAsync()).ActiveUserId);
        Assert.Equal(1, h.Model.Calls);
    }

    [Fact]
    public async Task Transient_provider_failures_retry_from_the_checkpoint_and_then_pause()
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Model.Failure = new GenerativeAiDependencyUnavailableException("busy");
        var run = await h.StartAsync("Hello");
        await h.StepAsync();
        await h.StepAsync();

        Assert.Equal(AssistantRunStatus.RetryWait, (await h.ViewAsync(run.Id)).Status);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            h.Clock.Advance(TimeSpan.FromSeconds(30));
            await h.StepAsync();
        }

        var paused = await h.ViewAsync(run.Id);
        Assert.Equal(AssistantRunStatus.Paused, paused.Status);
        Assert.Equal(3, h.Model.Calls);

        h.Model.Failure = null;
        h.Model.ThenText("Back.");
        await h.CommandAsync(run.Id, "resume");
        await h.DrainAsync();
        Assert.Equal(AssistantRunStatus.Completed, (await h.ViewAsync(run.Id)).Status);
        await using var db = h.Db();
        Assert.Equal(4, await db.AssistantModelInvocations.CountAsync());
    }

    [Fact]
    public async Task The_last_call_of_a_window_disables_tools_and_the_run_pauses_for_resume()
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Options.MaxModelCalls = 3;
        h.Restart();
        var page = 0;
        h.Model.Fallback = request => request.ToolChoice == AgentToolChoice.None
            ? ScriptedModel.Reply("I stopped; 2 batches remain.")
            : ScriptedModel.Reply(string.Empty, ("list_items", new { kind = "words", quiz_id = (string?)null, offset = page++ }));
        var run = await h.StartAsync("Read everything");
        await h.DrainAsync();

        var paused = await h.ViewAsync(run.Id);
        Assert.Equal(AssistantRunStatus.Paused, paused.Status);
        Assert.Equal(3, h.Model.Calls);
        var last = h.Model.Requests[^1];
        Assert.Equal(AgentToolChoice.None, last.ToolChoice);
        Assert.Contains("step budget", last.TrailingInstruction);
        Assert.Equal(JsonSerializer.Serialize(h.Model.Requests[0].Tools), JsonSerializer.Serialize(last.Tools));
        Assert.Equal("I stopped; 2 batches remain.", paused.Parts.Last(part => part.Type == AssistantPartTypes.Text).Text);

        h.Model.Fallback = _ => ScriptedModel.Reply("All done.");
        await h.CommandAsync(run.Id, "resume");
        await h.DrainAsync();
        Assert.Equal(AssistantRunStatus.Completed, (await h.ViewAsync(run.Id)).Status);
    }

    [Fact]
    public async Task A_call_repeated_with_identical_arguments_is_answered_without_running()
    {
        await using var h = await AssistantHarness.CreateAsync();
        var args = new { kind = "words", quiz_id = (string?)null, offset = (int?)null };
        h.Model.ThenCall("list_items", args).ThenCall("list_items", args).ThenCall("list_items", args).ThenText("Stopping.");

        var run = await h.RunAsync("List words");

        var tools = run.Parts.Where(part => part.Type == AssistantPartTypes.Tool).ToList();
        Assert.Equal([AssistantToolStates.Completed, AssistantToolStates.Completed, AssistantToolStates.Error], tools.Select(part => part.State));
        Assert.StartsWith("Skipped a repeated", tools[2].Title);
    }

    [Fact]
    public async Task An_edit_planned_before_the_user_changed_the_quiz_is_refused_as_a_conflict()
    {
        await using var h = await AssistantHarness.CreateAsync(db => db.Words.Add(new Word { Id = "w1", QuizId = Guid.Empty, Lemma = "dom", Translation = "house" }));
        h.Model.ThenCall("edit_items", new { quiz_id = (string?)null, words = new[] { new { id = "w1", word = (string?)null, translation = "home" } }, sentences = (object?)null })
            .ThenText("The quiz changed; I left it.");
        var run = await h.StartAsync("Change dom to home");
        await h.StepAsync();
        await h.StepAsync(); // planned against the current content
        await using (var db = h.Db())
        {
            (await db.Words.SingleAsync()).Translation = "the user's own edit";
            await db.SaveChangesAsync();
        }

        await h.DrainAsync();

        await using var after = h.Db();
        Assert.Equal("the user's own edit", (await after.Words.SingleAsync()).Translation);
        var tool = (await h.ViewAsync(run.Id)).Parts.Single(part => part.Type == AssistantPartTypes.Tool);
        Assert.Equal(AssistantToolStates.Error, tool.State);
        Assert.Equal("The quiz changed first", tool.Title);
    }

    [Fact]
    public async Task A_long_source_is_read_by_line_and_unread_lines_are_pointed_out_before_finishing()
    {
        await using var h = await AssistantHarness.CreateAsync();
        var source = string.Join("\n", Enumerable.Range(1, 300).Select(line => $"Linijka numer {line} z tekstu źródłowego."));
        h.Model.ThenText("Done.")
            .ThenCall("read_source", new { from_line = 41, to_line = (int?)null })
            .ThenCall("read_source", new { from_line = 191, to_line = 300 })
            .ThenText("Now really done.");

        var run = await h.RunAsync(source);

        Assert.Equal(AssistantRunStatus.Completed, run.Status);
        var user = TextOf(h.Model.Requests[0].History[1]);
        Assert.StartsWith("1: Linijka numer 1 ", user);
        Assert.Contains("300 lines in total", user);
        Assert.DoesNotContain("Linijka numer 41 ", user);
        Assert.Contains("You have not read lines 41–300", AssistantHarness.Transcript(h.Model.Requests[1]));
        Assert.Contains("lines 191–300", h.Model.Requests[2].TrailingInstruction);
        Assert.Null(h.Model.Requests[3].TrailingInstruction);
    }

    [Fact]
    public async Task Unfinished_checklist_items_get_a_reminder_before_the_run_ends()
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Model.ThenCall("update_plan", new { items = new object[] { new { text = "Add words", status = "completed" }, new { text = "Add sentences", status = "in_progress" } } })
            .ThenText("Done.")
            .ThenText("Sentences were not needed after all.");

        var run = await h.RunAsync("Add words and sentences");

        Assert.Equal(AssistantRunStatus.Completed, run.Status);
        Assert.Equal(3, h.Model.Calls);
        Assert.Equal("in_progress", run.Plan[1].Status);
        Assert.Contains("[in_progress] Add sentences", h.Model.Requests[1].TrailingInstruction);
        Assert.Contains("unfinished items: Add sentences", AssistantHarness.Transcript(h.Model.Requests[2]));
    }

    [Fact]
    public async Task Old_tool_output_is_cleared_when_history_outgrows_the_budget()
    {
        await using var h = await AssistantHarness.CreateAsync(db =>
        {
            for (var index = 0; index < 150; index++)
            {
                db.Words.Add(new Word { Id = $"w{index}", QuizId = Guid.Empty, Lemma = $"słowo{index:000} {new string('x', 40)}", Translation = $"word {index}" });
            }
        });
        h.Options.ContextTokenBudget = 8_000;
        h.Options.ProtectedToolOutputTokens = 1_000;
        h.Restart();
        h.Model.ThenCall("list_items", new { kind = "words", quiz_id = (string?)null, offset = (int?)null })
            .ThenCall("list_items", new { kind = "words", quiz_id = (string?)null, offset = 100 })
            .ThenText("Read them all.");

        await h.RunAsync("Read all words");

        var last = AssistantHarness.Transcript(h.Model.Requests[^1]);
        Assert.Contains("Earlier tool output cleared", last);
        await using var db = h.Db();
        Assert.Single(await db.AssistantParts.Where(part => part.CompactedAt != null).ToListAsync());
    }

    [Fact]
    public async Task Older_conversation_is_summarized_when_pruning_is_not_enough()
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Options.ContextTokenBudget = 8_000;
        h.Restart();
        h.Model.ThenText(new string('a', 12_000)).ThenText(new string('b', 12_000));
        await h.RunAsync("First question");
        await h.RunAsync("Second question");
        h.Model.Then(request =>
        {
            Assert.Equal(AssistantPrompts.Compaction, request.SystemInstruction);
            Assert.Empty(request.Tools);
            return ScriptedModel.Reply("The user asked two questions and got long answers.");
        }).ThenText("Third answer.");

        await h.RunAsync("Third question");

        var history = h.Model.Requests[^1].History;
        Assert.Contains("Summary of the earlier conversation", TextOf(history[0]));
        Assert.DoesNotContain(new string('a', 100), string.Join("\n", history.Select(turn => turn.ContentJson)));
        Assert.Contains("Third question", string.Join("\n", history.Select(TextOf)));
    }

    [Fact]
    public async Task A_new_quiz_is_private_and_building_until_the_run_finishes()
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Model.ThenCall("create_quiz", new { name = "Travel", source_language = (string?)null, target_language = (string?)null, collection_id = (string?)null, words = new[] { new { word = "pociąg", translation = "train" } }, sentences = (object?)null })
            .Then(request =>
            {
                Assert.Contains("You created quiz \"Travel\"", request.TrailingInstruction);
                return ScriptedModel.Reply("Created.");
            });
        var run = await h.StartAsync("Create a travel quiz", quizId: Guid.Empty);
        await h.StepAsync();
        await h.StepAsync();
        await h.StepAsync();

        await using (var db = h.Db())
        {
            var building = await db.Quizzes.SingleAsync(quiz => quiz.Name == "Travel");
            Assert.DoesNotContain((await h.ViewAsync(run.Id)).Parts, part => part.Type == AssistantPartTypes.QuizLink);
            Assert.Equal("Building", building.ProcessingStatus);
            Assert.False(building.IsPublic);
            Assert.Equal("Polish", building.TargetLanguage);
            Assert.Equal("English", building.SourceLanguage);
        }

        await h.DrainAsync();
        await using var after = h.Db();
        Assert.Equal("Ready", (await after.Quizzes.SingleAsync(quiz => quiz.Name == "Travel")).ProcessingStatus);
        var finished = await h.ViewAsync(run.Id);
        var artifact = Assert.Single(finished.Artifacts);
        var link = Assert.Single(finished.Parts, part => part.Type == AssistantPartTypes.QuizLink);
        Assert.Equal(artifact.Id.ToString(), link.Text);
        Assert.Equal("Travel", link.Title);
        Assert.Equal(link, finished.Parts.Last());
        var history = await h.WithAsync(services => services.GetRequiredService<AssistantThreadStore>()
            .GetChatHistoryAsync(run.ThreadId, AssistantHarness.UserId, default));
        Assert.Contains(history.Messages.SelectMany(message => message.Parts ?? []), part => part == link);
    }

    [Fact]
    public async Task A_quiz_bound_for_a_public_collection_is_published_only_after_approval()
    {
        var collectionId = Guid.NewGuid();
        await using var h = await AssistantHarness.CreateAsync(db => db.Collections.Add(new Collection
        {
            Id = collectionId, UserId = AssistantHarness.UserId, Name = "Shared", Language = "Polish", IsPublic = true,
        }));
        h.Model.ThenCall("create_quiz", new { name = "Travel", source_language = (string?)null, target_language = (string?)null, collection_id = collectionId.ToString(), words = (object?)null, sentences = (object?)null })
            .ThenText("Created.");
        var run = await h.StartAsync("Create a travel quiz in Shared", quizId: Guid.Empty);
        await h.DrainAsync();

        var waiting = await h.ViewAsync(run.Id);
        Assert.Equal(AssistantRunStatus.AwaitingApproval, waiting.Status);
        await using (var db = h.Db())
        {
            Assert.Null((await db.Quizzes.SingleAsync(quiz => quiz.Name == "Travel")).CollectionId);
        }

        await h.CommandAsync(run.Id, "approve");
        await h.DrainAsync();

        Assert.Equal(AssistantRunStatus.Completed, (await h.ViewAsync(run.Id)).Status);
        await using var after = h.Db();
        Assert.Equal(collectionId, (await after.Quizzes.SingleAsync(quiz => quiz.Name == "Travel")).CollectionId);
        Assert.Equal(2, h.Model.Calls);
    }

    [Fact]
    public async Task Undo_reverts_the_run_but_keeps_items_the_user_changed_afterwards()
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Model.ThenCall("add_items", new { quiz_id = (string?)null, words = new[] { new { word = "dom", translation = "house" }, new { word = "kot", translation = "cat" } }, sentences = (object?)null })
            .ThenText("Added.")
            .ThenText("Noted.");
        var run = await h.RunAsync("Add dom and kot");
        await using (var db = h.Db())
        {
            (await db.Words.SingleAsync(word => word.Lemma == "kot")).Translation = "a cat";
            await db.SaveChangesAsync();
        }

        var result = await h.UndoAsync(run.Id);

        Assert.Equal(new AssistantUndoResult(1, 1), result);
        await using var after = h.Db();
        Assert.Equal("kot", (await after.Words.SingleAsync()).Lemma);
        var view = await h.ViewAsync(run.Id);
        Assert.True(view.Undone);
        Assert.False(view.CanUndo);
        Assert.Equal(new AssistantUndoResult(0, 0), await h.UndoAsync(run.Id));
        await h.RunAsync("What happened?");
        Assert.Contains("undid this request's changes", AssistantHarness.Transcript(h.Model.Requests[^1]));
    }

    [Fact]
    public async Task Undo_removes_a_created_quiz_with_its_content()
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Model.ThenCall("create_quiz", new { name = "Travel", source_language = (string?)null, target_language = (string?)null, collection_id = (string?)null, words = new[] { new { word = "pociąg", translation = "train" } }, sentences = new[] { new { text = "To jest pociąg.", translation = "This is a train." } } })
            .ThenText("Created.");
        var run = await h.RunAsync("Create a travel quiz", quizId: Guid.Empty);

        Assert.Equal(new AssistantUndoResult(3, 0), await h.UndoAsync(run.Id));

        await using var db = h.Db();
        Assert.False(await db.Quizzes.AnyAsync(quiz => quiz.Name == "Travel"));
        Assert.Empty(await db.Words.ToListAsync());
        Assert.Empty(await db.QuizSentences.ToListAsync());
    }

    [Fact]
    public async Task A_sync_run_proposes_approval_changes_for_the_classic_apply_button()
    {
        await using var h = await AssistantHarness.CreateAsync(db => db.Words.Add(new Word { Id = "w1", QuizId = Guid.Empty, Lemma = "dom", Translation = "house" }));
        h.Model.ThenCall("delete_items", new { quiz_id = (string?)null, word_ids = new[] { "w1" }, sentence_ids = (string[]?)null })
            .ThenText("Review the removal and apply it.");

        var run = await h.RunAsync("Remove dom", mode: AssistantRunModes.Sync);

        Assert.Equal(AssistantRunStatus.Completed, run.Status);
        await using var db = h.Db();
        Assert.Single(await db.Words.ToListAsync());
        var message = await db.AssistantMessages.SingleAsync(candidate => candidate.Id == run.MessageId);
        Assert.Contains(PendingChangeKinds.DeleteWord, message.PendingChangesJson);
        Assert.Equal(AssistantMessageStatus.Active, message.Status);
    }

    [Fact]
    public async Task A_sync_run_turns_a_question_into_its_reply()
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Model.ThenCall("ask_user", new { question = "Which topic?", options = new[] { "Food", "Travel" }, multiple = (bool?)null });

        var run = await h.RunAsync("Make me a quiz", mode: AssistantRunModes.Sync);

        Assert.Equal(AssistantRunStatus.Completed, run.Status);
        Assert.Equal("Which topic?\n- Food\n- Travel", run.Parts.Last(part => part.Type == AssistantPartTypes.Text).Text);
    }

    [Fact]
    public async Task Submissions_are_idempotent_owned_and_one_active_run_per_user()
    {
        await using var h = await AssistantHarness.CreateAsync();
        var first = await h.StartAsync("Add dom", key: "same");
        var repeated = await h.StartAsync("Add dom", key: "same");

        Assert.Equal(first.Id, repeated.Id);
        await Assert.ThrowsAsync<AssistantRunConflictException>(() => h.StartAsync("Something else", key: "same"));
        await Assert.ThrowsAsync<AssistantRunConflictException>(() => h.StartAsync("Another request"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => h.WithAsync(services =>
            services.GetRequiredService<AssistantRunStore>().ViewAsync(first.Id, "other", default)));
        await using var db = h.Db();
        Assert.Single(await db.AssistantRuns.ToListAsync());
    }

    [Fact]
    public async Task A_new_request_elsewhere_stops_a_run_that_is_only_waiting_on_the_user()
    {
        var otherThread = Guid.NewGuid();
        await using var h = await AssistantHarness.CreateAsync(db => db.AssistantThreads.Add(new AssistantThread
        {
            Id = otherThread, UserId = AssistantHarness.UserId, Language = "Polish", Title = "Other",
        }));
        h.Model.ThenCall("ask_user", new { question = "Which topic?", options = (string[]?)null, multiple = (bool?)null }).ThenText("Hi.");
        var waiting = await h.StartAsync("Make me a quiz");
        await h.DrainAsync();

        var next = await h.StartAsync("Hello", threadId: otherThread);
        await h.DrainAsync();

        Assert.Equal(AssistantRunStatus.Cancelled, (await h.ViewAsync(waiting.Id)).Status);
        Assert.Equal(AssistantRunStatus.Completed, (await h.ViewAsync(next.Id)).Status);
    }

    [Fact]
    public async Task Analytics_record_each_model_call_and_tool_with_cached_tokens()
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Model.ThenCall("add_items", AddDom).ThenText("Added.");

        var run = await h.RunAsync("Add dom");

        await using var db = h.Db();
        var invocations = await db.AssistantModelInvocations.OrderBy(invocation => invocation.Sequence).ToListAsync();
        Assert.Equal([0, 1], invocations.Select(invocation => invocation.Sequence));
        Assert.All(invocations, invocation => Assert.Equal(600, invocation.CachedPromptTokens));
        var tool = await db.AssistantToolExecutions.SingleAsync();
        Assert.Equal("add_items", tool.ToolName);
        Assert.Equal(invocations[0].Id, tool.InvocationId);
        var stored = await db.AssistantRuns.SingleAsync();
        Assert.Equal(1200, stored.CachedInputTokens);
        Assert.Equal(run.TurnId, h.Model.Usage[0].AssistantTurnId);
    }

}
