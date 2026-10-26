using Glosify.Models.Entities;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Assistant.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantLanguageConsistencyTests
{
    [Fact]
    public async Task Clearing_the_selection_opens_a_matching_chat_and_preserves_language_history()
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Language.CurrentLanguage = null;
        await using (var db = h.Db())
        {
            (await db.Users.SingleAsync(user => user.Id == AssistantHarness.UserId)).SelectedQuizLanguageCode = null;
            await db.SaveChangesAsync();
        }

        Assert.Empty(await h.WithAsync(services => services.GetRequiredService<AssistantThreadStore>()
            .ListAsync(AssistantHarness.UserId, CancellationToken.None)));
        var thread = await h.WithAsync(services => services.GetRequiredService<AssistantThreadStore>()
            .GetOrCreateDefaultAsync(AssistantHarness.UserId, null, CancellationToken.None));
        Assert.NotEqual(h.ThreadId, thread.Id);
        Assert.Null(thread.Language);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.WithAsync(services =>
            services.GetRequiredService<AssistantThreadStore>().GetOwnedAsync(h.ThreadId, AssistantHarness.UserId, CancellationToken.None)));

        h.Model.ThenText("Choose a learning language.");
        var run = await h.WithAsync(services => services.GetRequiredService<AssistantRunStore>().StartAsync(
            thread.Id, AssistantHarness.UserId, new AssistantRunStartInput(Guid.NewGuid().ToString(),
                new AssistantRunInput("Hello", ExpectedLanguageCode: "")), CancellationToken.None));
        await h.DrainAsync();
        Assert.Equal(AssistantRunStatus.Completed, (await h.ViewAsync(run.Id)).Status);
        Assert.DoesNotContain("Learning language: Polish", AssistantHarness.Transcript(Assert.Single(h.Model.Requests)));

        h.Language.CurrentLanguage = "Polish";
        var restored = await h.WithAsync(services => services.GetRequiredService<AssistantThreadStore>()
            .ListAsync(AssistantHarness.UserId, CancellationToken.None));
        Assert.Equal(h.ThreadId, Assert.Single(restored).Id);
        await Assert.ThrowsAsync<ArgumentException>(() => h.WithAsync(services =>
            services.GetRequiredService<AssistantRunStore>().StartAsync(thread.Id, AssistantHarness.UserId,
                new AssistantRunStartInput(Guid.NewGuid().ToString(), new AssistantRunInput("Continue", ExpectedLanguageCode: "pl")),
                CancellationToken.None)));
    }

    [Theory]
    [InlineData("uk")]
    [InlineData("")]
    [InlineData("unknown")]
    public async Task Stale_or_invalid_picker_selection_is_rejected_before_starting_a_run(string displayedCode)
    {
        await using var h = await AssistantHarness.CreateAsync();
        var error = await Assert.ThrowsAsync<ArgumentException>(() => h.StartAsync("Hello",
            input: new AssistantRunInput("Hello", ExpectedLanguageCode: displayedCode)));
        Assert.Contains("learning language changed", error.Message);
        await using var db = h.Db();
        Assert.Empty(await db.AssistantRuns.ToListAsync());
        Assert.Empty(await db.AssistantMessages.ToListAsync());
        Assert.Empty(h.Model.Requests);
    }

    [Theory]
    [InlineData("pl")]
    [InlineData("de")]
    [InlineData(null)]
    public async Task Stale_follow_up_is_rejected_but_the_run_can_still_be_cancelled(string? displayedCode)
    {
        await using var h = await AssistantHarness.CreateAsync();
        var run = await h.StartAsync("Hello", input: new AssistantRunInput("Hello", ExpectedLanguageCode: "pl"));
        h.Language.CurrentLanguage = "German";
        await Assert.ThrowsAsync<ArgumentException>(() => h.WithAsync(services =>
            services.GetRequiredService<AssistantRunStore>().CommandAsync(run.Id, AssistantHarness.UserId, "steer",
                new AssistantRunCommand(run.Revision, "Continue", ExpectedLanguageCode: displayedCode), CancellationToken.None)));
        Assert.Equal(run.Revision, (await h.ViewAsync(run.Id)).Revision);
        Assert.Equal(AssistantRunStatus.Cancelled, (await h.CommandAsync(run.Id, "cancel")).Status);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Queued_run_keeps_the_language_selected_when_the_message_was_sent(bool withTranscript, bool legacyRun)
    {
        Guid? transcriptId = withTranscript ? Guid.NewGuid() : null;
        await using var h = await AssistantHarness.CreateAsync(db =>
        {
            if (transcriptId is Guid id) db.RealtimeTranslationTranscripts.Add(new RealtimeTranslationTranscript
            {
                Id = id, UserId = AssistantHarness.UserId, TargetLanguage = "pl", Title = "Polish conversation",
            });
        });
        h.Model.ThenCall("create_quiz", new { name = "Captured Polish", words = new[] { new { word = "dom", translation = "house" } } })
            .ThenText("Let's practice Polish.");
        var run = await h.StartAsync("Help me practice", input: new AssistantRunInput("Help me practice", TranscriptId: transcriptId));

        h.Language.CurrentLanguage = "German";
        await using (var db = h.Db())
        {
            (await db.Users.SingleAsync(user => user.Id == AssistantHarness.UserId)).SelectedQuizLanguageCode = "de";
            if (legacyRun)
            {
                var saved = await db.AssistantRuns.SingleAsync(candidate => candidate.Id == run.Id);
                var state = RunJson.Read<AssistantRunState>(saved.StateJson);
                state.LanguageSelection = null;
                saved.StateJson = RunJson.Write(state);
            }
            await db.SaveChangesAsync();
        }
        h.Restart();
        await h.DrainAsync();

        await using var after = h.Db();
        var facts = RunJson.Read<AssistantRunState>((await after.AssistantRuns.SingleAsync(r => r.Id == run.Id)).StateJson).Facts!;
        Assert.Equal("Polish", facts.TargetLanguage);
        Assert.Equal("pl", facts.TargetLanguageCode);
        Assert.Equal(transcriptId, facts.TranscriptId);
        Assert.Contains("Learning language: Polish", AssistantHarness.Transcript(h.Model.Requests[0]));
        Assert.DoesNotContain("Learning language: German", AssistantHarness.Transcript(h.Model.Requests[0]));
        Assert.Equal("Polish", (await after.Quizzes.SingleAsync(quiz => quiz.Name == "Captured Polish")).TargetLanguage);
    }

    [Fact]
    public async Task An_empty_selection_is_captured_and_not_replaced_by_a_later_preference()
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Language.CurrentLanguage = null;
        await using (var db = h.Db())
        {
            (await db.Users.SingleAsync(user => user.Id == AssistantHarness.UserId)).SelectedQuizLanguageCode = null;
            (await db.AssistantThreads.SingleAsync(thread => thread.Id == h.ThreadId)).Language = null;
            await db.SaveChangesAsync();
        }
        h.Model.ThenText("Choose a learning language.");
        var run = await h.StartAsync("Hello", input: new AssistantRunInput("Hello", ExpectedLanguageCode: ""));
        h.Language.CurrentLanguage = "German";
        await h.DrainAsync();

        await using var after = h.Db();
        var state = RunJson.Read<AssistantRunState>((await after.AssistantRuns.SingleAsync(r => r.Id == run.Id)).StateJson);
        Assert.NotNull(state.LanguageSelection);
        Assert.Null(state.Facts!.TargetLanguage);
        Assert.Null(state.Facts.TargetLanguageCode);
        Assert.DoesNotContain("Learning language: German", AssistantHarness.Transcript(Assert.Single(h.Model.Requests)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Explicit_context_in_another_language_cannot_override_the_picker(bool anki, bool legacyRun)
    {
        var id = Guid.NewGuid();
        await using var h = await AssistantHarness.CreateAsync(db =>
        {
            if (anki) db.AnkiCollections.Add(new AnkiCollection { Id = id, UserId = AssistantHarness.UserId, TargetLanguage = "German" });
            else db.Quizzes.Add(new Quiz { Id = id, UserId = AssistantHarness.UserId, Name = "German", TargetLanguage = "German" });
        });
        var input = anki
            ? new AssistantRunInput("Help me practice", ContextAnkiCollectionId: id)
            : new AssistantRunInput("Help me practice", ContextQuizId: id);
        var run = await h.StartAsync(input.Message, input: input);
        if (legacyRun)
        {
            // A legacy request with no captured/thread/account language cannot use
            // attached content to invent a selection that the picker does not show.
            h.Language.CurrentLanguage = null;
            await using var db = h.Db();
            (await db.Users.SingleAsync(user => user.Id == AssistantHarness.UserId)).SelectedQuizLanguageCode = null;
            (await db.AssistantThreads.SingleAsync(thread => thread.Id == h.ThreadId)).Language = null;
            var saved = await db.AssistantRuns.SingleAsync(candidate => candidate.Id == run.Id);
            var state = RunJson.Read<AssistantRunState>(saved.StateJson);
            state.LanguageSelection = null;
            saved.StateJson = RunJson.Write(state);
            await db.SaveChangesAsync();
        }
        await h.DrainAsync();

        var result = await h.ViewAsync(run.Id);
        Assert.Equal(AssistantRunStatus.Failed, result.Status);
        Assert.Contains("selected learning language", result.Reason);
        Assert.Empty(h.Model.Requests);
    }

    [Fact]
    public async Task An_old_implicit_anki_context_cannot_change_the_learning_language()
    {
        var germanCollection = Guid.NewGuid();
        await using var h = await AssistantHarness.CreateAsync(db =>
            db.AnkiCollections.Add(new AnkiCollection { Id = germanCollection, UserId = AssistantHarness.UserId, TargetLanguage = "German" }));
        h.Model.ThenText("Hello.").ThenText("Let's practice Polish.");
        var old = await h.StartAsync("Hello", input: new AssistantRunInput("Hello"));
        await h.DrainAsync();
        await using (var db = h.Db())
        {
            // A legacy run could retain a collection from another language in this chat.
            var saved = await db.AssistantRuns.SingleAsync(run => run.Id == old.Id);
            var state = RunJson.Read<AssistantRunState>(saved.StateJson);
            state.Facts = state.Facts! with { AnkiCollectionId = germanCollection };
            saved.StateJson = RunJson.Write(state);
            await db.SaveChangesAsync();
        }
        var next = await h.StartAsync("Continue", input: new AssistantRunInput("Continue"));
        await h.DrainAsync();

        await using var after = h.Db();
        var facts = RunJson.Read<AssistantRunState>((await after.AssistantRuns.SingleAsync(run => run.Id == next.Id)).StateJson).Facts!;
        Assert.Equal("Polish", facts.TargetLanguage);
        Assert.Equal("pl", facts.TargetLanguageCode);
        Assert.Null(facts.AnkiCollectionId);
        Assert.Contains("Learning language: Polish", AssistantHarness.Transcript(h.Model.Requests.Last()));
    }
}
