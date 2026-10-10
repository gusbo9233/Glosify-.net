using System.Text.Json;
using Glosify.Data;
using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Models.Library;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Assistant.Runtime;
using Glosify.Services.Ai.Assistant.Tools;
using Glosify.Services.Anki;
using Glosify.Services.Learning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantLearningTests
{
    private static readonly Guid CollectionId = Guid.Parse("b1111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Mistakes_use_current_ids_and_content_and_exclude_skips_legacy_deleted_foreign_and_old_history()
    {
        await using var h = await SeedAsync();
        await using (var db = h.Db())
        {
            db.QuizAttempts.AddRange(Attempt(h, "w1", false), Attempt(h, "w1", false), Attempt(h, "w1", true),
                Attempt(h, "w2", false), Attempt(h, "w2", false, skipped: true), Attempt(h, null, false),
                Attempt(h, "deleted", false), Attempt(h, "w2", false, days: 40), Attempt(h, "w2", false, user: "other"));
            (await db.Words.SingleAsync(w => w.Id == "w1")).Lemma = "updated dom";
            await db.SaveChangesAsync();
        }
        var result = await h.WithAsync(sp => sp.GetRequiredService<ILearningInsightsService>().MistakesAsync("user", "Polish", null, "words", 30, 0, 1));
        var first = Assert.Single(result.Items);
        Assert.Equal("w1", first.ItemId);
        Assert.Equal("updated dom", first.Text);
        Assert.Equal(2, first.Mistakes);
        Assert.Equal(3, first.Observations);
        Assert.Equal(2, result.TotalCount);
        Assert.Equal(1, result.NextOffset);
        Assert.Equal(1, result.UnidentifiedAttemptItems);
        var second = await h.WithAsync(sp => sp.GetRequiredService<ILearningInsightsService>().MistakesAsync("user", "Polish", null, "words", 30, 1, 1));
        Assert.Equal("w2", Assert.Single(second.Items).ItemId);
        Assert.Null(second.NextOffset);
        var empty = await h.WithAsync(sp => sp.GetRequiredService<ILearningInsightsService>().MistakesAsync("other", "Polish", null, null, 30, 0, 20));
        Assert.Empty(empty.Items);
        await Assert.ThrowsAsync<ArgumentException>(() => h.WithAsync(sp => sp.GetRequiredService<ILearningInsightsService>().ProgressAsync("other", "Polish", h.QuizId, 30)));
    }

    [Fact]
    public async Task Progress_separates_accuracy_retention_and_skips_with_read_only_forecast()
    {
        await using var h = await SeedAsync();
        await AddAsync(h);
        await using (var db = h.Db())
        {
            db.QuizAttempts.AddRange(Attempt(h, "w1", true), Attempt(h, "w2", false), Attempt(h, "w1", false, skipped: true));
            var card = await db.AnkiCards.FirstAsync();
            card.State = AnkiCardStates.Review;
            card.DueAt = h.Clock.GetUtcNow().AddDays(1);
            db.AnkiReviews.AddRange(Review(h, card.Id, "again"), Review(h, card.Id, "good"), Review(h, card.Id, "easy"));
            await db.SaveChangesAsync();
        }
        var progress = await h.WithAsync(sp => sp.GetRequiredService<ILearningInsightsService>().ProgressAsync("user", "Polish", h.QuizId, 30));
        Assert.Equal(50, progress.QuizAccuracyPercent);
        Assert.Equal(66.7, progress.AnkiRetentionPercent);
        Assert.Equal(1, progress.Skipped);
        Assert.Equal(3, progress.AnkiReviews);
        var forecast = await h.WithAsync(sp => sp.GetRequiredService<IAnkiStatisticsService>().ReadSnapshotAsync(CollectionId, "user"));
        Assert.NotNull(forecast);
        Assert.Equal(14, forecast.DueForecast.Count);
        Assert.Equal(1, forecast.DueForecast[1].Value);
        Assert.Equal(1, forecast.DueForecast.Sum(p => p.Value));
        Assert.Equal(3, forecast.ReviewsLast30Days);
        var mistakes = await h.WithAsync(sp => sp.GetRequiredService<ILearningInsightsService>().MistakesAsync("user", "Polish", null, null, 30, 0, 20));
        Assert.Equal(1, mistakes.Items.Sum(i => i.AnkiLapses));
        await using var read = h.Db();
        var tools = AssistantToolFactory.Create(read);
        var result = await tools.RunAsync("get_learning_progress", JsonSerializer.Serialize(new { collection_id = CollectionId }), Context(h));
        Assert.False(result.IsError);
        var output = JsonSerializer.SerializeToElement(result.Output);
        Assert.Contains("DueForecast", output.GetProperty("collection_progress").GetRawText());
        Assert.Empty(read.ChangeTracker.Entries());
        Assert.Equal(3, await read.AnkiReviews.CountAsync());
        var noHistory = await h.WithAsync(sp => sp.GetRequiredService<ILearningInsightsService>().ProgressAsync("other", "Polish", null, 30));
        Assert.Null(noHistory.QuizAccuracyPercent);
        Assert.Null(noHistory.AnkiRetentionPercent);
    }

    [Fact]
    public async Task Chat_adds_observed_difficult_words_to_anki_then_creates_targeted_quiz_and_examples()
    {
        await using var h = await SeedAsync();
        await using (var db = h.Db()) { db.QuizAttempts.Add(Attempt(h, "w2", false)); await db.SaveChangesAsync(); }
        h.Model.ThenCall("get_learning_mistakes", new { kind = "words", limit = 1 })
            .Then(request =>
            {
                Assert.Contains("w2", AssistantHarness.Transcript(request));
                return ScriptedModel.Reply("", ("add_anki_items", new { collection_id = CollectionId, kind = "words", item_ids = new[] { "w2" } }));
            }).ThenCall("create_quiz", new { name = "Focused practice", source_language = "English", target_language = "Polish",
                words = new[] { new { word = "kot", translation = "cat" } },
                sentences = new[] { new { text = "To jest kot.", translation = "This is a cat." } } }).ThenText("Saved targeted practice and an example sentence.");
        var run = await h.RunAsync("Add my weakest word to Revision and make a quiz with an example sentence.");
        Assert.Equal(AssistantRunStatus.Completed, run.Status);
        await using var after = h.Db();
        Assert.Equal("w2", (await after.AnkiNotes.SingleAsync()).WordId);
        var quiz = await after.Quizzes.SingleAsync(q => q.Name == "Focused practice");
        Assert.Equal("kot", (await after.Words.SingleAsync(w => w.QuizId == quiz.Id)).Lemma);
        Assert.Equal("To jest kot.", (await after.QuizSentences.SingleAsync(s => s.QuizId == quiz.Id)).Text);
    }

    [Fact]
    public async Task Rename_is_saved_and_undo_preserves_subsequent_edits()
    {
        await using var h = await SeedAsync();
        h.Model.ThenCall("rename_anki_collection", new { collection_id = CollectionId, name = "Week one" }).ThenText("Renamed.");
        var run = await h.RunAsync("Rename Revision to Week one");
        Assert.Equal(AssistantRunStatus.Completed, run.Status);
        await using (var db = h.Db()) Assert.Equal("Week one", (await db.AnkiCollections.SingleAsync()).Name);
        Assert.Equal(1, (await h.UndoAsync(run.Id)).Undone);
        await using (var db = h.Db()) Assert.Equal("Revision", (await db.AnkiCollections.SingleAsync()).Name);
        h.Model.ThenCall("rename_anki_collection", new { collection_id = CollectionId, name = "Week two" }).ThenText("Renamed.");
        var second = await h.RunAsync("Rename to Week two");
        await h.WithAsync(sp => sp.GetRequiredService<IAnkiCollectionService>().RenameAsync(CollectionId, "My edit", "user"));
        Assert.Equal(0, (await h.UndoAsync(second.Id)).Undone);
        await using var after = h.Db();
        Assert.Equal("My edit", (await after.AnkiCollections.SingleAsync()).Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Card_removal_requires_approval_preserves_reviews_and_can_be_undone(bool approve)
    {
        await using var h = await SeedAsync();
        await h.WithAsync(sp => sp.GetRequiredService<IAnkiCollectionService>().LinkQuizAdditiveAsync(new(CollectionId, h.QuizId, true, false, false, false), "user"));
        Guid id;
        await using (var db = h.Db())
        {
            var card = await db.AnkiCards.FirstAsync(); id = card.Id;
            card.ReviewCount = 3; card.DueAt = h.Clock.GetUtcNow().AddDays(2);
            db.AnkiReviews.Add(Review(h, id, "good")); await db.SaveChangesAsync();
        }
        h.Model.ThenCall("remove_anki_cards", new { collection_id = CollectionId, card_ids = new[] { id } }).ThenText("Done.");
        var run = await h.RunAsync("Remove this card from study");
        Assert.Equal(AssistantRunStatus.AwaitingApproval, run.Status);
        Assert.Contains("Review history retained", Assert.Single(run.Approval!.Changes).Summary);
        await using (var db = h.Db()) Assert.True((await db.AnkiCards.SingleAsync(c => c.Id == id)).IsActive);
        await h.CommandAsync(run.Id, approve ? "approve" : "reject"); await h.DrainAsync();
        await h.WithAsync(async sp => { await sp.GetRequiredService<IAnkiCollectionService>().SyncCollectionAsync(CollectionId); return true; });
        await using (var db = h.Db())
        {
            var card = await db.AnkiCards.SingleAsync(c => c.Id == id);
            Assert.Equal(!approve, card.IsActive);
            Assert.Equal(approve, card.ExcludedFromQuizLink);
            Assert.Equal(3, card.ReviewCount); Assert.Single(await db.AnkiReviews.ToListAsync());
        }
        if (approve)
        {
            Assert.Equal(1, (await h.UndoAsync(run.Id)).Undone);
            await using var db = h.Db();
            var card = await db.AnkiCards.SingleAsync(c => c.Id == id);
            Assert.True(card.IsActive); Assert.False(card.ExcludedFromQuizLink);
            Assert.Equal(3, card.ReviewCount); Assert.Equal(h.Clock.GetUtcNow().AddDays(2), card.DueAt);
        }
    }

    [Fact]
    public async Task Unlink_leaves_direct_cards_and_history_and_undo_restores_link()
    {
        await using var h = await SeedAsync();
        await AddAsync(h);
        await h.WithAsync(sp => sp.GetRequiredService<IAnkiCollectionService>().LinkQuizAdditiveAsync(new(CollectionId, h.QuizId, true, false, false, false), "user"));
        h.Model.ThenCall("unlink_anki_quiz", new { collection_id = CollectionId }).ThenText("Unlinked.");
        var run = await h.RunAsync("Stop syncing this quiz with Revision");
        Assert.Equal(AssistantRunStatus.AwaitingApproval, run.Status);
        await h.CommandAsync(run.Id, "approve"); await h.DrainAsync();
        await using (var db = h.Db())
        {
            Assert.Empty(await db.AnkiQuizLinks.ToListAsync());
            Assert.Equal("w1", (await db.AnkiCards.Include(c => c.Note).SingleAsync(c => c.IsActive)).Note.WordId);
            Assert.Equal(2, await db.AnkiCards.CountAsync());
        }
        Assert.Equal(1, (await h.UndoAsync(run.Id)).Undone);
        await using var after = h.Db();
        Assert.Single(await after.AnkiQuizLinks.ToListAsync());
        Assert.Equal(2, await after.AnkiCards.CountAsync(c => c.IsActive));
    }

    [Theory]
    [InlineData("remove_anki_cards")]
    [InlineData("unlink_anki_quiz")]
    public async Task A_review_after_proposal_prevents_stale_maintenance(string tool)
    {
        await using var h = await SeedAsync();
        await h.WithAsync(sp => sp.GetRequiredService<IAnkiCollectionService>().LinkQuizAdditiveAsync(new(CollectionId, h.QuizId, true, false, false, false), "user"));
        Guid id;
        await using (var db = h.Db()) id = (await db.AnkiCards.FirstAsync()).Id;
        object args = tool == "remove_anki_cards" ? new { collection_id = CollectionId, card_ids = new[] { id } } : new { collection_id = CollectionId };
        h.Model.ThenCall(tool, args).ThenText("The card changed; please review again.");
        var run = await h.RunAsync("Remove from study");
        Assert.Equal(AssistantRunStatus.AwaitingApproval, run.Status);
        await using (var db = h.Db()) { (await db.AnkiCards.SingleAsync(c => c.Id == id)).ReviewCount++; await db.SaveChangesAsync(); }
        await h.CommandAsync(run.Id, "approve"); await h.DrainAsync();
        await using var after = h.Db();
        Assert.Equal(2, await after.AnkiCards.CountAsync(c => c.IsActive));
        Assert.Single(await after.AnkiQuizLinks.ToListAsync());
        Assert.Empty(await after.AssistantChanges.ToListAsync());
    }

    [Fact]
    public async Task Study_links_validate_selection_and_prefer_due_anki_without_writes()
    {
        await using var h = await SeedAsync();
        await AddAsync(h);
        await using var db = h.Db();
        var dueCollection = Guid.NewGuid();
        db.AnkiCollections.Add(new() { Id = dueCollection, Name = "Due first", UserId = "user", SourceLanguage = "English", TargetLanguage = "Polish" });
        await db.SaveChangesAsync();
        await h.WithAsync(sp => sp.GetRequiredService<IAnkiCollectionService>().AddItemsAsync(new(dueCollection, h.QuizId, "words", ["w2"], true, false), "user"));
        var due = await db.AnkiCards.SingleAsync(c => c.Note.AnkiCollectionId == dueCollection);
        due.State = AnkiCardStates.Review; due.DueAt = h.Clock.GetUtcNow().AddDays(-90);
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var tools = AssistantToolFactory.Create(db);
        var auto = await tools.RunAsync("prepare_study_session", "{\"minutes\":10}", Context(h));
        Assert.False(auto.IsError);
        var output = JsonSerializer.SerializeToElement(auto.Output);
        Assert.Equal($"/Anki/Study/{dueCollection}", output.GetProperty("url").GetString());
        Assert.True(output.GetProperty("time_is_estimate").GetBoolean());
        var quiz = await tools.RunAsync("prepare_study_session", "{\"mode\":\"typing\",\"item_ids\":[\"w2\"]}", Context(h));
        var url = JsonSerializer.SerializeToElement(quiz.Output).GetProperty("url").GetString();
        Assert.Contains($"/TypingQuiz?id={h.QuizId}", url); Assert.Contains("selectedWordIds=w2", url);
        foreach (var args in new[] { "{\"minutes\":0}", "{\"mode\":\"typing\",\"item_ids\":[\"foreign\"]}", "{\"mode\":\"typing\",\"kind\":\"sentences\",\"item_ids\":[\"w2\"]}", "{\"mode\":\"anki\",\"item_ids\":[\"w2\"]}" })
            Assert.True((await tools.RunAsync("prepare_study_session", args, Context(h))).IsError);
        Assert.Empty(db.ChangeTracker.Entries()); Assert.Empty(await db.QuizAttempts.ToListAsync()); Assert.Empty(await db.AnkiReviews.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_to_quiz_to_anki_uses_saved_ids_and_undoes_the_entire_workflow(bool transcript)
    {
        var sourceId = Guid.NewGuid();
        await using var h = await AssistantHarness.CreateAsync(db =>
        {
            if (transcript)
                db.RealtimeTranslationTranscripts.Add(new RealtimeTranslationTranscript
                {
                    Id = sourceId, UserId = "user", TargetLanguage = "pl", Title = "Conversation", Stream = "source",
                    Segments = [new() { Id = Guid.NewGuid(), SessionId = Guid.NewGuid(), Sequence = 1, Text = "Ala ma kota.", Stream = "source", ProviderEventKey = "one" }]
                });
            else
            {
                db.BookDocuments.Add(new BookDocument { Id = sourceId, UserId = "user", Title = "Course", PageCount = 1, BlobName = "b", OriginalFileName = "b.pdf" });
                db.BookPages.Add(new BookPage { Id = Guid.NewGuid(), BookDocumentId = sourceId, PageNumber = 1, Text = "Ala ma kota." });
            }
        });
        Guid quizId = default, collectionId = default;
        object sourceArgs = transcript ? new { transcript_id = sourceId, page = 1 } : new { book_id = sourceId, from_page = 1 };
        h.Model.ThenCall(transcript ? "get_saved_transcript" : "get_book_pages", sourceArgs)
            .Then(request =>
            {
                Assert.Contains("Ala ma kota.", AssistantHarness.Transcript(request));
                return ScriptedModel.Reply("", ("create_quiz", new { name = "From source", source_language = "English", target_language = "Polish",
                    words = new[] { new { word = "kot", translation = "cat" } } }));
            }).Then(request =>
            {
                quizId = Output(request).GetProperty("quiz_id").GetGuid();
                return ScriptedModel.Reply("", ("create_anki_collection", new { name = "Source revision", quiz_id = quizId }));
            }).Then(request =>
            {
                collectionId = Output(request).GetProperty("anki_collection_id").GetGuid();
                return ScriptedModel.Reply("", ("list_items", new { kind = "words", quiz_id = quizId }));
            }).Then(request => ScriptedModel.Reply("", ("add_anki_items", new { collection_id = collectionId, quiz_id = quizId,
                kind = "words", item_ids = Output(request).GetProperty("words").EnumerateArray().Select(w => w.GetProperty("id").GetString()).ToArray() })))
            .ThenText("Created the quiz and added its vocabulary to Source revision.");
        var run = await h.RunAsync("Read the first page, make a vocabulary quiz and create an Anki collection from it.");
        Assert.Equal(AssistantRunStatus.Completed, run.Status);
        await using (var db = h.Db())
        {
            Assert.Equal(quizId, (await db.AnkiNotes.SingleAsync()).QuizId);
            Assert.Equal(collectionId, (await db.AnkiNotes.SingleAsync()).AnkiCollectionId);
            Assert.Single(await db.AnkiCards.ToListAsync()); Assert.Empty(await db.AnkiQuizLinks.ToListAsync());
        }
        Assert.Equal(4, (await h.UndoAsync(run.Id)).Undone);
        await using var after = h.Db();
        Assert.Empty(await after.AnkiCollections.ToListAsync()); Assert.False(await after.Quizzes.AnyAsync(q => q.Id == quizId));
        Assert.Equal(1, transcript ? await after.RealtimeTranslationTranscripts.CountAsync() : await after.BookDocuments.CountAsync());
    }

    [Fact]
    public async Task Maintenance_and_study_reject_foreign_collections_and_mixed_card_ids_without_changes()
    {
        await using var h = await SeedAsync();
        await AddAsync(h);
        await using var db = h.Db();
        var foreign = Guid.NewGuid();
        db.AnkiCollections.Add(new() { Id = foreign, UserId = "other", Name = "Private", SourceLanguage = "English", TargetLanguage = "Polish" });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var tools = AssistantToolFactory.Create(db);
        var ownedCard = (await db.AnkiCards.AsNoTracking().SingleAsync()).Id;
        foreach (var (tool, args) in new (string, object)[]
        {
            ("rename_anki_collection", new { collection_id = foreign, name = "Stolen" }),
            ("remove_anki_cards", new { collection_id = foreign, card_ids = new[] { ownedCard } }),
            ("unlink_anki_quiz", new { collection_id = foreign }),
            ("prepare_study_session", new { collection_id = foreign }),
            ("get_learning_progress", new { collection_id = foreign }),
            ("remove_anki_cards", new { collection_id = CollectionId, card_ids = new[] { ownedCard, Guid.NewGuid() } })
        })
        {
            var result = await tools.RunAsync(tool, JsonSerializer.Serialize(args), Context(h));
            Assert.True(result.IsError); Assert.Empty(result.Changes);
        }
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.True((await db.AnkiCards.SingleAsync()).IsActive);
        Assert.Equal("Private", (await db.AnkiCollections.SingleAsync(c => c.Id == foreign)).Name);
    }

    [Fact]
    public async Task Sentence_mistakes_remain_distinct_from_words_and_legacy_aggregate_history_is_explicit()
    {
        await using var h = await SeedAsync();
        var sentenceId = Guid.NewGuid();
        await using (var db = h.Db())
        {
            db.QuizSentences.Add(new() { Id = sentenceId, QuizId = h.QuizId, Text = "To jest dom.", Translation = "This is a house." });
            var attempt = Attempt(h, sentenceId.ToString(), false); attempt.PracticeItemType = "sentences";
            var legacy = Attempt(h, null, false); legacy.Items.Clear();
            db.QuizAttempts.AddRange(attempt, legacy, Attempt(h, "w1", false));
            await db.SaveChangesAsync();
        }
        var result = await h.WithAsync(sp => sp.GetRequiredService<ILearningInsightsService>().MistakesAsync("user", "Polish", h.QuizId, "sentences", 30, 0, 20));
        var item = Assert.Single(result.Items);
        Assert.Equal(sentenceId.ToString(), item.ItemId); Assert.Equal("To jest dom.", item.Text);
        Assert.Equal("sentences", item.ItemType); Assert.Equal(1, result.SummaryOnlyQuizAttempts);
    }

    [Fact]
    public async Task Automatic_quiz_fallback_skips_empty_quizzes()
    {
        await using var h = await SeedAsync();
        await using var db = h.Db();
        db.Quizzes.Add(new() { Id = Guid.NewGuid(), Name = "A empty", UserId = "user", TargetLanguage = "Polish", SourceLanguage = "English", Language = "Polish" });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var tools = AssistantToolFactory.Create(db);
        var result = await tools.RunAsync("prepare_study_session", "{}", Context(h) with { QuizId = null });
        Assert.False(result.IsError);
        var output = JsonSerializer.SerializeToElement(result.Output);
        Assert.Equal(h.QuizId, output.GetProperty("quiz_id").GetGuid());
        Assert.Equal(2, output.GetProperty("suggested_items").GetInt32());
        Assert.Empty(db.ChangeTracker.Entries());
    }

    private static JsonElement Output(Glosify.Services.Ai.Generation.AgentRequest request)
    {
        foreach (var turn in request.History.Reverse())
        {
            using var json = JsonDocument.Parse(turn.ContentJson);
            if (!json.RootElement.TryGetProperty("parts", out var parts)) continue;
            foreach (var part in parts.EnumerateArray().Reverse())
                if (part.TryGetProperty("responseJson", out var response)) return JsonDocument.Parse(response.GetString()!).RootElement.Clone();
        }
        throw new InvalidOperationException("No tool output.");
    }

    private static Task<AssistantHarness> SeedAsync() => AssistantHarness.CreateAsync(db =>
    {
        db.AnkiCollections.Add(new AnkiCollection { Id = CollectionId, Name = "Revision", UserId = "user", SourceLanguage = "English", TargetLanguage = "Polish" });
        db.Words.AddRange(new Word { Id = "w1", Lemma = "dom", Translation = "house" }, new Word { Id = "w2", Lemma = "kot", Translation = "cat" });
    });
    private static Task<AnkiAdditionResult> AddAsync(AssistantHarness h) => h.WithAsync(sp => sp.GetRequiredService<IAnkiCollectionService>().AddItemsAsync(new(CollectionId, h.QuizId, "words", ["w1"], true, false), "user"));
    private static ToolContext Context(AssistantHarness h) => new() { UserId = "user", Mode = AssistantMode.Language, TargetLanguage = "Polish", QuizId = h.QuizId };
    private static QuizAttempt Attempt(AssistantHarness h, string? id, bool correct, bool skipped = false, int days = 1, string user = "user") => new()
    {
        Id = Guid.NewGuid(), QuizId = h.QuizId, UserId = user, Mode = "typing", PracticeItemType = "words", TotalItems = 1,
        CorrectCount = correct ? 1 : 0, IncorrectCount = !correct && !skipped ? 1 : 0, SkippedCount = skipped ? 1 : 0,
        StartedAt = h.Clock.GetUtcNow().AddDays(-days), CompletedAt = h.Clock.GetUtcNow().AddDays(-days),
        Items = [new() { Id = Guid.NewGuid(), ItemId = id, Prompt = "old text", ExpectedAnswer = "old answer", IsCorrect = correct, IsSkipped = skipped }]
    };
    private static AnkiReview Review(AssistantHarness h, Guid cardId, string rating) => new()
    {
        Id = Guid.NewGuid(), ClientToken = Guid.NewGuid(), AnkiCollectionId = CollectionId, AnkiCardId = cardId, Rating = rating,
        ReviewedAt = h.Clock.GetUtcNow().AddHours(-1), NewDueAt = h.Clock.GetUtcNow().AddDays(1)
    };
}
