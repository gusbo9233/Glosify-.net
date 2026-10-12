using System.Text.Json;
using Glosify.Services.Anki;
using Microsoft.Extensions.DependencyInjection;
using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Assistant.Runtime;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantLearningManagementTests
{
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
            collectionId = (await service.CreateAsync(new("Study", "English", "Polish", "UTC"), AssistantHarness.UserId)).Id;
            await service.AddQuizAsync(new(collectionId, h.QuizId, true, false, false, false), AssistantHarness.UserId);
        }
        h.Model.ThenCall("manage_anki", JsonSerializer.Serialize(new { action = "direction", collection_id = collectionId, direction = "target-to-source" })).ThenText("Reversed.");
        var run = await h.RunAsync("Reverse my collection");
        Assert.Equal(1, run.SavedChanges);
        await using (var db = h.Db()) Assert.Equal(PracticeDirection.TargetToSource, (await db.AnkiCards.SingleAsync(c => c.IsActive)).Direction);
        if (reviewBeforeUndo)
        {
            await using var scope = h.Scope();
            var study = new AnkiStudyService(scope.ServiceProvider.GetRequiredService<Glosify.Data.GlosifyContext>(),
                scope.ServiceProvider.GetRequiredService<IAnkiCollectionService>(), new Fsrs6AnkiScheduler(), h.Clock);
            var card = (await study.GetNextAsync(collectionId, AssistantHarness.UserId))!.Card!;
            Assert.True(await study.RateAsync(new(collectionId, card.CardId, "good", Guid.NewGuid(), card.RowVersion, 500), AssistantHarness.UserId));
        }
        var undo = await h.UndoAsync(run.Id);
        Assert.Equal(reviewBeforeUndo ? 0 : 1, undo.Undone);
        await using (var db = h.Db())
        {
            var card = await db.AnkiCards.SingleAsync(c => c.IsActive);
            Assert.Equal(reviewBeforeUndo ? PracticeDirection.TargetToSource : PracticeDirection.SourceToTarget, card.Direction);
            Assert.Equal(reviewBeforeUndo ? 1 : 0, card.ReviewCount);
        }
    }

    [Fact]
    public async Task Creating_anki_from_a_selected_collection_creates_a_new_collection()
    {
        await using var h = await AssistantHarness.CreateAsync();
        Guid selected;
        await using (var scope = h.Scope()) selected = (await scope.ServiceProvider.GetRequiredService<IAnkiCollectionService>().CreateAsync(new("Existing", "English", "Polish", "UTC"), AssistantHarness.UserId)).Id;
        h.Model.ThenCall("manage_anki", "{\"action\":\"create\",\"name\":\"New study\"}").ThenText("Created.");
        var run = await h.StartAsync("Create another study collection", input: new AssistantRunInput("Create another study collection", ContextAnkiCollectionId: selected));
        await h.DrainAsync();
        Assert.Equal(1, (await h.ViewAsync(run.Id)).SavedChanges);
        await using var db = h.Db();
        var collections = await db.AnkiCollections.ToListAsync();
        Assert.Equal(2, collections.Count);
        Assert.Contains(collections, c => c.Id == selected && c.Name == "Existing");
        Assert.Contains(collections, c => c.Id != selected && c.Name == "New study");
    }

    [Fact]
    public async Task Anki_settings_undo_keeps_intervening_settings_edits()
    {
        await using var h = await AssistantHarness.CreateAsync();
        Guid id;
        await using (var scope = h.Scope()) id = (await scope.ServiceProvider.GetRequiredService<IAnkiCollectionService>().CreateAsync(new("Study", "English", "Polish", "UTC"), AssistantHarness.UserId)).Id;
        h.Model.ThenCall("manage_anki", JsonSerializer.Serialize(new { action = "settings", collection_id = id, new_cards_per_day = 12 })).ThenText("Updated.");
        var run = await h.RunAsync("Change daily limit");
        Assert.Equal(1, run.SavedChanges);
        await using (var db = h.Db()) { (await db.AnkiCollections.SingleAsync()).NewCardsPerDay = 15; await db.SaveChangesAsync(); }
        var undo = await h.UndoAsync(run.Id);
        Assert.Equal(0, undo.Undone);
        await using (var db = h.Db()) Assert.Equal(15, (await db.AnkiCollections.SingleAsync()).NewCardsPerDay);
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

    [Fact]
    public async Task Private_quiz_cannot_be_copied_or_disclosed_by_public_tools()
    {
        var other = Guid.NewGuid();
        await using var h = await AssistantHarness.CreateAsync(db => { db.Quizzes.Add(new Quiz { Id = other, UserId = "other", Name = "Private", SourceLanguage = "English", TargetLanguage = "Polish", Language = "Polish" }); });
        h.Model.ThenCall("manage_quiz_library", JsonSerializer.Serialize(new { action = "copy_quiz", id = other })).ThenText("Not found.");
        var run = await h.RunAsync("Copy this quiz");
        Assert.Empty(run.ResourceChanges);
    }
}
