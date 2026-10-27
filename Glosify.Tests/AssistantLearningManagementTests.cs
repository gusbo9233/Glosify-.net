using System.Text.Json;
using Glosify.Services.Anki;
using Microsoft.Extensions.DependencyInjection;
using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Assistant.Runtime;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantLearningManagementTests
{
    [Theory]
    [InlineData("copy_quiz", "quiz")]
    [InlineData("copy_collection", "collection")]
    [InlineData("quiz_visibility", "quiz")]
    [InlineData("collection_visibility", "collection")]
    public async Task Historical_sharing_journals_can_still_be_undone(string kind, string type)
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Model.ThenText("Done.");
        var run = await h.RunAsync("Hello");
        var id = Guid.NewGuid();
        await using (var db = h.Db())
        {
            if (type == "quiz") db.Quizzes.Add(new Quiz { Id = id, UserId = AssistantHarness.UserId, Name = "Legacy", SourceLanguage = "English", TargetLanguage = "Polish", Language = "Polish" });
            else db.Collections.Add(new Collection { Id = id, UserId = AssistantHarness.UserId, Name = "Legacy", Language = "Polish" });
            await db.SaveChangesAsync();
            var before = await LearningChanges.SnapshotAsync(db, type, id, AssistantHarness.UserId, default);
            if (kind.EndsWith("visibility", StringComparison.Ordinal))
            {
                if (type == "quiz") (await db.Quizzes.SingleAsync(q => q.Id == id)).IsPublic = true;
                else (await db.Collections.SingleAsync(c => c.Id == id)).IsPublic = true;
                await db.SaveChangesAsync();
            }
            var after = await LearningChanges.SnapshotAsync(db, type, id, AssistantHarness.UserId, default);
            var part = await db.AssistantParts.FirstAsync(p => p.RunId == run.Id);
            db.AssistantChanges.Add(new AssistantChange
            {
                Id = Guid.NewGuid(), RunId = run.Id, PartId = part.Id, UserId = AssistantHarness.UserId,
                Kind = kind, EntityType = type, EntityId = id.ToString(),
                BeforeJson = kind.StartsWith("copy_", StringComparison.Ordinal) ? null : RunJson.Write(before),
                AfterJson = RunJson.Write(after), CreatedAt = h.Clock.GetUtcNow().UtcDateTime,
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, (await h.UndoAsync(run.Id)).Undone);
        await using var result = h.Db();
        if (kind.StartsWith("copy_", StringComparison.Ordinal))
        {
            Assert.False(await result.Quizzes.AnyAsync(q => q.Id == id));
            Assert.False(await result.Collections.AnyAsync(c => c.Id == id));
        }
        else if (type == "quiz") Assert.False((await result.Quizzes.SingleAsync(q => q.Id == id)).IsPublic);
        else Assert.False((await result.Collections.SingleAsync(c => c.Id == id)).IsPublic);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Undo_edit_keeps_later_content_with_the_original_text(bool sentence)
    {
        await using var h = await AssistantHarness.CreateAsync();
        var id = Guid.NewGuid();
        await using (var db = h.Db())
        {
            if (sentence) db.QuizSentences.Add(new QuizSentence { Id = id, QuizId = h.QuizId, Text = "Original", Translation = "Translation" });
            else db.Words.Add(new Word { Id = id.ToString(), QuizId = h.QuizId, Lemma = "Original", Translation = "Translation" });
            await db.SaveChangesAsync();
        }
        var args = sentence
            ? JsonSerializer.Serialize(new { sentences = new[] { new { id, text = "Changed" } } })
            : JsonSerializer.Serialize(new { words = new[] { new { id, word = "Changed" } } });
        h.Model.ThenCall("edit_items", args).ThenText("Updated.");
        var run = await h.RunAsync("Change the text");
        Assert.Equal(1, run.SavedChanges);
        await using (var db = h.Db())
        {
            if (sentence) db.QuizSentences.Add(new QuizSentence { Id = Guid.NewGuid(), QuizId = h.QuizId, Text = "Original", Translation = "Later content" });
            else db.Words.Add(new Word { Id = Guid.NewGuid().ToString(), QuizId = h.QuizId, Lemma = "Original", Translation = "Later content" });
            await db.SaveChangesAsync();
        }
        Assert.Equal(0, (await h.UndoAsync(run.Id)).Undone);
        await using var after = h.Db();
        if (sentence)
        {
            Assert.Equal("Changed", (await after.QuizSentences.SingleAsync(s => s.Id == id)).Text);
            Assert.Equal(1, await after.QuizSentences.CountAsync(s => s.Text == "Original"));
        }
        else
        {
            Assert.Equal("Changed", (await after.Words.SingleAsync(w => w.Id == id.ToString())).Lemma);
            Assert.Equal(1, await after.Words.CountAsync(w => w.Lemma == "Original"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Anki_direction_undo_preserves_intervening_reviews(bool reviewBeforeUndo)
    {
        await using var h = await AssistantHarness.CreateAsync();
        Guid collectionId;
        await using (var scope = h.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Glosify.Data.GlosifyContext>();
            db.Words.Add(new Word { Id = "dom", QuizId = h.QuizId, Lemma = "dom", Translation = "house" });
            await db.SaveChangesAsync();
            var service = scope.ServiceProvider.GetRequiredService<IAnkiCollectionService>();
            collectionId = (await service.GetOrCreateAsync(AssistantHarness.UserId, "Polish", "UTC")).Id;
            await service.AddQuizAsync(new(collectionId, h.QuizId, true, true, false, false), AssistantHarness.UserId);
        }
        h.Model.ThenCall("manage_anki", JsonSerializer.Serialize(new { action = "direction", collection_id = collectionId, direction = "target-to-source" })).ThenText("Reversed.");
        var run = await h.RunAsync("Reverse my collection");
        Assert.Equal(1, run.SavedChanges);
        await using (var db = h.Db())
        {
            // Direction is the practice preference; both cards stay in the collection.
            Assert.Equal(PracticeDirection.TargetToSource, (await db.AnkiCollections.SingleAsync()).DefaultDirection);
            Assert.Equal(2, await db.AnkiCards.CountAsync(c => c.IsActive));
        }
        if (reviewBeforeUndo)
        {
            await using var scope = h.Scope();
            var study = new AnkiStudyService(scope.ServiceProvider.GetRequiredService<Glosify.Data.GlosifyContext>(),
                scope.ServiceProvider.GetRequiredService<IAnkiCollectionService>(), new Fsrs6AnkiScheduler(), h.Clock);
            var card = (await study.GetNextAsync(collectionId, AssistantHarness.UserId, PracticeDirection.TargetToSource))!.Card!;
            Assert.True(await study.RateAsync(new(collectionId, card.CardId, "good", Guid.NewGuid(), card.RowVersion, 500), AssistantHarness.UserId));
        }
        var undo = await h.UndoAsync(run.Id);
        Assert.Equal(reviewBeforeUndo ? 0 : 1, undo.Undone);
        await using (var db = h.Db())
        {
            Assert.Equal(reviewBeforeUndo ? PracticeDirection.TargetToSource : PracticeDirection.SourceToTarget,
                (await db.AnkiCollections.SingleAsync()).DefaultDirection);
            Assert.Equal(reviewBeforeUndo ? 1 : 0, (await db.AnkiCards.SingleAsync(c => c.Direction == PracticeDirection.TargetToSource)).ReviewCount);
        }
    }

    [Fact]
    public async Task Manage_anki_cannot_create_a_second_collection_for_a_language()
    {
        await using var h = await AssistantHarness.CreateAsync();
        Guid selected;
        await using (var scope = h.Scope()) selected = (await scope.ServiceProvider.GetRequiredService<IAnkiCollectionService>().GetOrCreateAsync(AssistantHarness.UserId, "Polish", "UTC")).Id;
        h.Model.ThenCall("manage_anki", "{\"action\":\"create\"}").ThenText("I can't create another collection.");
        var run = await h.StartAsync("Create another study collection", input: new AssistantRunInput("Create another study collection", ContextAnkiCollectionId: selected));
        await h.DrainAsync();
        Assert.Equal(0, (await h.ViewAsync(run.Id)).SavedChanges);
        await using var db = h.Db();
        Assert.Equal(selected, (await db.AnkiCollections.SingleAsync()).Id);
    }

    [Theory]
    [InlineData("desired_retention", 0.8)]
    [InlineData("new_cards_per_day", 12)]
    [InlineData("maximum_reviews_per_day", 100)]
    public async Task Assistant_cannot_customize_shared_study_defaults(string field, double value)
    {
        await using var h = await AssistantHarness.CreateAsync();
        Guid id;
        await using (var scope = h.Scope())
            id = (await scope.ServiceProvider.GetRequiredService<IAnkiCollectionService>()
                .GetOrCreateAsync(AssistantHarness.UserId, "Polish", "UTC")).Id;
        h.Model.ThenCall("manage_anki", JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["action"] = "settings", ["collection_id"] = id, [field] = value,
        })).ThenText("Study limits use shared defaults.");

        var run = await h.RunAsync("Change my study limits");

        Assert.Equal(0, run.SavedChanges);
        await using var db = h.Db();
        var collection = await db.AnkiCollections.SingleAsync();
        Assert.Equal(AnkiStudyDefaults.DesiredRetention, collection.DesiredRetention);
        Assert.Equal(int.MaxValue, collection.NewCardsPerDay);
        Assert.Equal(int.MaxValue, collection.MaximumReviewsPerDay);
    }

    [Fact]
    public async Task Previously_queued_study_limit_changes_are_rejected_without_changing_timezone()
    {
        await using var h = await AssistantHarness.CreateAsync();
        await using var db = h.Db();
        var collections = new AnkiCollectionService(db, h.Clock);
        var collection = await collections.GetOrCreateAsync(AssistantHarness.UserId, "Polish", "UTC");
        var snapshot = await LearningChanges.SnapshotAsync(db, "anki", collection.Id, AssistantHarness.UserId,
            default, collectionOnly: true);
        var payload = JsonSerializer.SerializeToElement(new
        {
            args = new { action = "settings", desiredRetention = 0.8, timeZoneId = "Europe/Stockholm" },
            entity_type = "anki", id = collection.Id, snapshot_scope = "collection",
            expected = LearningChanges.Hash(snapshot),
        });
        var applier = new LearningChanges(db, null!, null!, collections);

        await Assert.ThrowsAsync<AnkiValidationException>(() =>
            applier.ApplyAsync(new PendingChange("anki_settings", payload), AssistantHarness.UserId, default));

        var saved = await db.AnkiCollections.AsNoTracking().SingleAsync();
        Assert.Equal("UTC", saved.TimeZoneId);
        Assert.Equal(AnkiStudyDefaults.DesiredRetention, saved.DesiredRetention);
    }

    [Fact]
    public async Task Anki_timezone_undo_keeps_intervening_timezone_edits()
    {
        await using var h = await AssistantHarness.CreateAsync();
        Guid id;
        await using (var scope = h.Scope()) id = (await scope.ServiceProvider.GetRequiredService<IAnkiCollectionService>().GetOrCreateAsync(AssistantHarness.UserId, "Polish", "UTC")).Id;
        h.Model.ThenCall("manage_anki", JsonSerializer.Serialize(new { action = "settings", collection_id = id, time_zone_id = "Europe/Stockholm" })).ThenText("Updated.");
        var run = await h.RunAsync("Change timezone");
        Assert.Equal(1, run.SavedChanges);
        await using (var db = h.Db()) { (await db.AnkiCollections.SingleAsync()).TimeZoneId = "America/New_York"; await db.SaveChangesAsync(); }
        var undo = await h.UndoAsync(run.Id);
        Assert.Equal(0, undo.Undone);
        await using (var db = h.Db()) Assert.Equal("America/New_York", (await db.AnkiCollections.SingleAsync()).TimeZoneId);
    }

    [Fact]
    public async Task Delete_quiz_waits_for_approval_then_reports_permanent_committed_change()
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Model.ThenCall("manage_quiz_library", JsonSerializer.Serialize(new { action = "delete_quiz", id = h.QuizId })).ThenText("Deleted.");
        var run = await h.RunAsync("Delete this quiz");
        Assert.Equal(AssistantRunStatus.AwaitingApproval, run.Status);
        Assert.Empty(run.ResourceChanges);
        await using (var db = h.Db()) Assert.True(await db.Quizzes.AnyAsync(q => q.Id == h.QuizId));
        await h.CommandAsync(run.Id, "approve");
        await h.DrainAsync();
        run = await h.ViewAsync(run.Id);
        Assert.Equal(AssistantRunStatus.Completed, run.Status);
        Assert.False(run.CanUndo);
        var history = await h.OrchestrateAsync(chats => chats.GetChatHistoryAsync(run.ThreadId, AssistantHarness.UserId));
        Assert.DoesNotContain(history.Messages, message => message.CanUndo);
        Assert.Equal(1, run.PermanentChanges);
        Assert.NotEmpty(run.ChangeRevision);
        Assert.Equal("delete_quiz", Assert.Single(run.ResourceChanges).Kind);
        await using (var db = h.Db())
        {
            Assert.False(await db.Quizzes.AnyAsync(q => q.Id == h.QuizId));
            var thread = await db.AssistantThreads.SingleAsync();
            Assert.Null(thread.QuizId);
            Assert.Null(thread.ContextQuizId);
        }
    }

    [Fact]
    public async Task Rename_is_saved_and_undo_updates_resource_revision()
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Model.ThenCall("manage_quiz_library", JsonSerializer.Serialize(new { action = "rename_quiz", id = h.QuizId, name = "New name" })).ThenText("Renamed.");
        var run = await h.RunAsync("Rename the quiz");
        Assert.Equal(1, run.SavedChanges);
        Assert.True(run.CanUndo);
        await using (var db = h.Db()) Assert.Equal("New name", (await db.Quizzes.SingleAsync()).Name);
        await h.UndoAsync(run.Id);
        var undone = await h.ViewAsync(run.Id);
        Assert.NotEqual(run.ChangeRevision, undone.ChangeRevision);
        await using (var db = h.Db()) Assert.Equal("Polish basics", (await db.Quizzes.SingleAsync()).Name);
    }

    [Fact]
    public async Task Approval_does_not_delete_a_quiz_changed_since_preview()
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Model.ThenCall("manage_quiz_library", JsonSerializer.Serialize(new { action = "delete_quiz", id = h.QuizId })).ThenText("The quiz changed.");
        var run = await h.RunAsync("Delete this quiz");
        await using (var db = h.Db()) { (await db.Quizzes.SingleAsync()).Name = "Edited elsewhere"; await db.SaveChangesAsync(); }
        await h.CommandAsync(run.Id, "approve");
        await h.DrainAsync();
        await using (var db = h.Db()) Assert.Equal("Edited elsewhere", (await db.Quizzes.SingleAsync()).Name);
        Assert.Empty((await h.ViewAsync(run.Id)).ResourceChanges);
    }

    [Theory]
    [InlineData("copy_quiz")]
    [InlineData("copy_collection")]
    [InlineData("quiz_visibility")]
    [InlineData("collection_visibility")]
    public async Task Retired_public_library_actions_cannot_change_content(string action)
    {
        var other = Guid.NewGuid();
        await using var h = await AssistantHarness.CreateAsync(db => { db.Quizzes.Add(new Quiz { Id = other, UserId = "other", Name = "Private", SourceLanguage = "English", TargetLanguage = "Polish", Language = "Polish" }); });
        h.Model.ThenCall("manage_quiz_library", JsonSerializer.Serialize(new { action, id = other })).ThenText("Not found.");
        var run = await h.RunAsync("Copy this quiz");
        Assert.Empty(run.ResourceChanges);
        await using var after = h.Db();
        Assert.False((await after.Quizzes.SingleAsync(q => q.Id == other)).IsPublic);
        Assert.Equal(1, await after.Quizzes.CountAsync(q => q.Name == "Private"));
    }
}
