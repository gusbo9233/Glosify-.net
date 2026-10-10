using System.Text.Json;
using Glosify.Data;
using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Assistant.Runtime;
using Glosify.Services.Ai.Assistant.Tools;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Anki;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantAnkiTests
{
    private static readonly Guid CollectionId = Guid.Parse("a1111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Chat_discovers_collection_and_adds_exact_first_twenty_once_in_quiz_order_then_undoes()
    {
        await using var h = await SeedAsync(25);
        string[] selected = [];
        h.Model.ThenCall("list_anki_collections", new { })
            .Then(request =>
            {
                Assert.Contains("Revision", AssistantHarness.Transcript(request));
                return ScriptedModel.Reply("", ("get_anki_collection", new { collection_id = CollectionId }));
            })
            .ThenCall("list_items", new { kind = "words", order = "created" })
            .Then(request =>
            {
                selected = Output(request).GetProperty("words").EnumerateArray().Take(20).Select(w => w.GetProperty("id").GetString()!).ToArray();
                Assert.Equal(Enumerable.Range(0, 20).Select(Id), selected);
                return ScriptedModel.Reply("", ("add_anki_items", new { collection_id = CollectionId, kind = "words", item_ids = selected }));
            })
            .Then(request =>
            {
                var output = Output(request);
                Assert.Equal(20, output.GetProperty("anki_cards_added").GetInt32());
                Assert.Equal($"/Anki/Collection/{CollectionId}", output.GetProperty("anki_url").GetString());
                return ScriptedModel.Reply("", ("add_anki_items", new { collection_id = CollectionId, kind = "words", item_ids = selected }));
            })
            .Then(request =>
            {
                Assert.Equal(0, Output(request).GetProperty("anki_cards_added").GetInt32());
                Assert.Equal(20, Output(request).GetProperty("anki_already_included").GetInt32());
                return ScriptedModel.Reply("Added 20 cards to Revision.");
            });
        var run = await h.RunAsync("Add the first 20 words of this quiz to my Anki Revision collection");
        Assert.Equal(AssistantRunStatus.Completed, run.Status);
        Assert.True(run.CanUndo);
        await using (var db = h.Db())
        {
            Assert.Equal(selected.Order(), (await db.AnkiNotes.Select(n => n.WordId!).ToListAsync()).Order());
            Assert.Equal(25, await db.Words.CountAsync());
            Assert.Empty(await db.AnkiQuizLinks.ToListAsync());
            Assert.All(await db.AnkiCards.ToListAsync(), c => Assert.Equal(PracticeDirection.SourceToTarget, c.Direction));
        }
        Assert.Equal(20, (await h.UndoAsync(run.Id)).Undone);
        await using var after = h.Db();
        Assert.Empty(await after.AnkiCards.ToListAsync());
        Assert.Empty(await after.AnkiNotes.ToListAsync());
        Assert.Single(await after.AnkiCollections.ToListAsync());
    }

    [Fact]
    public async Task Creation_uses_quiz_language_pair_and_undo_removes_only_its_new_collection()
    {
        await using var h = await SeedAsync(2);
        Guid createdId = default;
        h.Model.ThenCall("create_anki_collection", new { name = "New revision" })
            .Then(request =>
            {
                createdId = Output(request).GetProperty("anki_collection_id").GetGuid();
                return ScriptedModel.Reply("", ("add_anki_items", new { collection_id = createdId, kind = "words", item_ids = new[] { Id(0), Id(1) }, direction = "both" }));
            }).ThenText("Created the collection and added four cards.");
        var run = await h.RunAsync("Create a new Anki collection for these two words in both directions");
        Assert.Equal(AssistantRunStatus.Completed, run.Status);
        await using (var db = h.Db())
        {
            var collection = await db.AnkiCollections.SingleAsync(c => c.Id == createdId);
            Assert.Equal("Polish", collection.TargetLanguage);
            Assert.Equal("English", collection.SourceLanguage);
            Assert.Equal("UTC", collection.TimeZoneId);
            Assert.Equal(4, await db.AnkiCards.CountAsync());
        }
        Assert.Equal(5, (await h.UndoAsync(run.Id)).Undone);
        await using var after = h.Db();
        Assert.False(await after.AnkiCollections.AnyAsync(c => c.Id == createdId));
        Assert.Equal(CollectionId, (await after.AnkiCollections.SingleAsync()).Id);
    }

    [Fact]
    public async Task Restarted_run_commits_anki_call_once_and_reports_actual_counts()
    {
        await using var h = await SeedAsync(1);
        h.Model.ThenCall("add_anki_items", new { collection_id = CollectionId, kind = "words", item_ids = new[] { Id(0) } }).ThenText("Added.");
        var run = await h.StartAsync("Add this word to Revision");
        await h.StepAsync();
        await h.StepAsync();
        h.Restart();
        await h.StepAsync();
        h.Restart();
        await h.DrainAsync();
        Assert.Equal(AssistantRunStatus.Completed, (await h.ViewAsync(run.Id)).Status);
        await using var db = h.Db();
        Assert.Single(await db.AnkiCards.ToListAsync());
        Assert.Single(await db.AssistantChanges.ToListAsync());
    }

    [Fact]
    public async Task Undo_keeps_reviewed_cards_and_reverts_other_additions()
    {
        await using var h = await SeedAsync(2);
        h.Model.ThenCall("add_anki_items", new { collection_id = CollectionId, kind = "words", item_ids = new[] { Id(0), Id(1) } }).ThenText("Added.");
        var run = await h.RunAsync("Add both words");
        Guid reviewed;
        await using (var db = h.Db())
        {
            var card = await db.AnkiCards.Include(c => c.Note).SingleAsync(c => c.Note.WordId == Id(0));
            reviewed = card.Id;
            card.ReviewCount = 1;
            card.LastReviewedAt = Now;
            card.State = AnkiCardStates.Review;
            card.Stability = 5;
            db.AnkiReviews.Add(new AnkiReview { Id = Guid.NewGuid(), AnkiCollectionId = CollectionId, AnkiCardId = card.Id,
                ClientToken = Guid.NewGuid(), Rating = "good", PreviousState = "new", NewState = "review", NewDueAt = Now.AddDays(5), ReviewedAt = Now, SchedulerVersion = "fsrs6" });
            await db.SaveChangesAsync();
        }
        var undo = await h.UndoAsync(run.Id);
        Assert.Equal(1, undo.Undone);
        Assert.Equal(1, undo.Kept);
        await using var after = h.Db();
        Assert.Equal(reviewed, (await after.AnkiCards.SingleAsync()).Id);
        Assert.Single(await after.AnkiReviews.ToListAsync());
    }

    [Theory]
    [InlineData("foreign_collection")]
    [InlineData("foreign_quiz")]
    [InlineData("wrong_pair")]
    [InlineData("missing_item")]
    [InlineData("wrong_quiz_item")]
    public async Task Invalid_batches_leave_no_partial_cards(string scenario)
    {
        await using var h = await SeedAsync(2);
        var quizId = h.QuizId;
        var ids = new[] { Id(0), Id(1) };
        await using (var db = h.Db())
        {
            if (scenario == "foreign_collection") (await db.AnkiCollections.SingleAsync()).UserId = "other";
            if (scenario == "foreign_quiz") (await db.Quizzes.SingleAsync()).UserId = "other";
            if (scenario == "wrong_pair") (await db.AnkiCollections.SingleAsync()).SourceLanguage = "Swedish";
            if (scenario == "missing_item") ids[1] = "gone";
            if (scenario == "wrong_quiz_item")
            {
                var second = new Quiz { Id = Guid.NewGuid(), UserId = AssistantHarness.UserId, Name = "Other", SourceLanguage = "English", TargetLanguage = "Polish", Language = "Polish" };
                db.Quizzes.Add(second);
                (await db.Words.SingleAsync(w => w.Id == Id(1))).QuizId = second.Id;
            }
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<AnkiValidationException>(() => h.WithAsync(sp => sp.GetRequiredService<IAnkiCollectionService>()
            .AddItemsAsync(new(CollectionId, quizId, PracticeItemType.Words, ids, true, false), AssistantHarness.UserId)));
        await using var after = h.Db();
        Assert.Empty(await after.AnkiCards.ToListAsync());
        Assert.Empty(await after.AnkiNotes.ToListAsync());
    }

    [Fact]
    public async Task Failure_during_save_rolls_back_sync_and_batch()
    {
        await using var h = await SeedAsync(2);
        // This stale link would cause synchronization to create cards before the batch saves.
        await using (var db = h.Db())
        {
            db.AnkiQuizLinks.Add(new AnkiQuizLink { Id = Guid.NewGuid(), AnkiCollectionId = CollectionId, QuizId = h.QuizId, WordsTargetToSource = true });
            await db.SaveChangesAsync();
        }
        h.Interceptors.Add(new FailDirectCardSave());
        await Assert.ThrowsAsync<InvalidOperationException>(() => AddAsync(h, Id(0), Id(1)));
        await using var after = h.Db();
        Assert.Empty(await after.AnkiCards.ToListAsync());
        Assert.Empty(await after.AnkiNotes.ToListAsync());
        Assert.Single(await after.AnkiQuizLinks.ToListAsync());
    }

    [Fact]
    public async Task Read_tools_are_owned_paginated_and_do_not_synchronize()
    {
        await using var h = await SeedAsync(2);
        await using (var db = h.Db())
        {
            db.AnkiQuizLinks.Add(new AnkiQuizLink { Id = Guid.NewGuid(), AnkiCollectionId = CollectionId, QuizId = h.QuizId, WordsSourceToTarget = true });
            for (var i = 0; i < 53; i++) db.AnkiCollections.Add(Collection(Guid.NewGuid(), $"Study {i:D2}"));
            var foreign = Collection(Guid.NewGuid(), "Foreign");
            foreign.UserId = "other";
            db.AnkiCollections.Add(foreign);
            await db.SaveChangesAsync();
        }
        await h.WithAsync(async sp =>
        {
            var service = sp.GetRequiredService<IAnkiCollectionService>();
            var first = await service.BrowseAsync(AssistantHarness.UserId, 0);
            Assert.Equal(54, first.TotalCount);
            Assert.Equal(50, first.Items.Count);
            Assert.Equal(50, first.NextOffset);
            Assert.DoesNotContain(first.Items, c => c.Name == "Foreign");
            var last = await service.BrowseAsync(AssistantHarness.UserId, first.NextOffset!.Value);
            Assert.Equal(4, last.Items.Count);
            Assert.Null(last.NextOffset);
            Assert.Null(await service.InspectAsync(CollectionId, "other", 0, 0));
            var details = await service.InspectAsync(CollectionId, AssistantHarness.UserId, 0, 0);
            Assert.Empty(details!.Cards.Items);
            Assert.Single(details.Links.Items);
            return true;
        });
        await using var after = h.Db();
        Assert.Empty(await after.AnkiCards.ToListAsync());
        Assert.Empty(await after.AnkiNotes.ToListAsync());
    }

    [Fact]
    public async Task Linking_preserves_exclusions_directions_and_progress_and_syncs_future_content()
    {
        await using var h = await SeedAsync(2);
        await h.WithAsync(async sp =>
        {
            var anki = sp.GetRequiredService<IAnkiCollectionService>();
            await anki.AddQuizAsync(new(CollectionId, h.QuizId, true, false, false, false), AssistantHarness.UserId);
            var db = sp.GetRequiredService<GlosifyContext>();
            var cards = await db.AnkiCards.Include(c => c.Note).OrderBy(c => c.Note.WordId).ToListAsync();
            await anki.RemoveCardAsync(cards[0].Id, AssistantHarness.UserId);
            cards[1].ReviewCount = 4;
            cards[1].Stability = 12;
            await db.SaveChangesAsync();
            var result = await anki.LinkQuizAdditiveAsync(new(CollectionId, h.QuizId, false, true, false, false), AssistantHarness.UserId);
            Assert.Equal(2, result.CardsAdded);
            Assert.NotNull(result.Link);
            var repeated = await anki.LinkQuizAdditiveAsync(new(CollectionId, h.QuizId, false, true, false, false), AssistantHarness.UserId);
            Assert.Equal(0, repeated.CardsAdded);
            Assert.Equal(2, repeated.AlreadyIncluded);
            Assert.Null(repeated.Link);
            var both = await anki.LinkQuizAdditiveAsync(new(CollectionId, h.QuizId, true, true, false, false), AssistantHarness.UserId);
            Assert.Equal(1, both.ExcludedCards);
            Assert.Equal(3, both.AlreadyIncluded);
            db.Words.Add(new Word { Id = "future", QuizId = h.QuizId, Lemma = "new", Translation = "new", CreatedAt = Now.AddDays(1) });
            await db.SaveChangesAsync();
            await anki.SyncQuizAsync(h.QuizId);
            return true;
        });
        await using var after = h.Db();
        var link = await after.AnkiQuizLinks.SingleAsync();
        Assert.True(link.WordsSourceToTarget && link.WordsTargetToSource);
        var excluded = await after.AnkiCards.SingleAsync(c => c.Note.WordId == Id(0) && c.Direction == PracticeDirection.SourceToTarget);
        Assert.True(excluded.ExcludedFromQuizLink);
        Assert.False(excluded.IsActive);
        var reviewed = await after.AnkiCards.SingleAsync(c => c.Note.WordId == Id(1) && c.Direction == PracticeDirection.SourceToTarget);
        Assert.Equal(4, reviewed.ReviewCount);
        Assert.Equal(12, reviewed.Stability);
        Assert.Equal(2, await after.AnkiCards.CountAsync(c => c.Note.WordId == "future" && c.IsActive));
    }

    [Fact]
    public async Task Adding_a_linked_card_records_direct_membership_without_resetting_it_and_undo_restores_the_link()
    {
        await using var h = await SeedAsync(1);
        await h.WithAsync(sp => sp.GetRequiredService<IAnkiCollectionService>()
            .LinkQuizAdditiveAsync(new(CollectionId, h.QuizId, true, false, false, false), AssistantHarness.UserId));
        await using (var db = h.Db())
        {
            var card = await db.AnkiCards.SingleAsync();
            card.ReviewCount = 3;
            card.Stability = 9;
            await db.SaveChangesAsync();
        }
        h.Model.ThenCall("add_anki_items", new { collection_id = CollectionId, kind = "words", item_ids = new[] { Id(0) } }).Then(request =>
        {
            Assert.Equal(0, Output(request).GetProperty("anki_cards_added").GetInt32());
            Assert.Equal(1, Output(request).GetProperty("anki_already_included").GetInt32());
            return ScriptedModel.Reply("Included directly.");
        });
        var run = await h.RunAsync("Add this word individually to Revision");
        await using (var db = h.Db())
        {
            var card = await db.AnkiCards.SingleAsync();
            Assert.True(card.DirectlyIncluded);
            Assert.True(card.QuizLinkIncluded);
            Assert.Equal(3, card.ReviewCount);
            Assert.Equal(9, card.Stability);
        }
        Assert.Equal(1, (await h.UndoAsync(run.Id)).Undone);
        await using var after = h.Db();
        var restored = await after.AnkiCards.SingleAsync();
        Assert.False(restored.DirectlyIncluded);
        Assert.True(restored.QuizLinkIncluded);
        Assert.True(restored.IsActive);
        Assert.Equal(3, restored.ReviewCount);
        Assert.Equal(9, restored.Stability);
    }

    [Fact]
    public async Task Undo_link_restores_previous_link_and_preserves_direct_cards()
    {
        await using var h = await SeedAsync(2);
        await AddAsync(h, Id(0));
        await h.WithAsync(sp => sp.GetRequiredService<IAnkiCollectionService>().LinkQuizAdditiveAsync(new(CollectionId, h.QuizId, true, false, false, false), AssistantHarness.UserId));
        h.Model.ThenCall("link_anki_quiz", new { collection_id = CollectionId, content = "words", direction = "both" }).ThenText("Linked.");
        var run = await h.RunAsync("Link this quiz in both directions");
        Assert.Equal(AssistantRunStatus.Completed, run.Status);
        Assert.Equal(1, (await h.UndoAsync(run.Id)).Undone);
        await using var after = h.Db();
        var link = await after.AnkiQuizLinks.SingleAsync();
        Assert.True(link.WordsSourceToTarget);
        Assert.False(link.WordsTargetToSource);
        Assert.Equal(2, await after.AnkiCards.CountAsync());
        Assert.True((await after.AnkiCards.SingleAsync(c => c.Note.WordId == Id(0))).DirectlyIncluded);
    }

    [Fact]
    public async Task Undo_link_keeps_future_synced_cards_and_later_user_changes()
    {
        await using var h = await SeedAsync(1);
        h.Model.ThenCall("link_anki_quiz", new { collection_id = CollectionId, content = "words" }).ThenText("Linked.");
        var run = await h.RunAsync("Link quiz");
        await h.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<GlosifyContext>();
            db.Words.Add(new Word { Id = "future", QuizId = h.QuizId, Lemma = "later", Translation = "later" });
            await db.SaveChangesAsync();
            await sp.GetRequiredService<IAnkiCollectionService>().SyncQuizAsync(h.QuizId);
            return true;
        });
        var undone = await h.UndoAsync(run.Id);
        Assert.Equal(0, undone.Undone);
        Assert.Equal(1, undone.Kept);
        await using var after = h.Db();
        Assert.Equal(2, await after.AnkiCards.CountAsync(c => c.IsActive));
        Assert.Single(await after.AnkiQuizLinks.ToListAsync());
    }

    [Fact]
    public async Task Sentences_use_collection_default_and_both_directions_without_duplicate_notes()
    {
        await using var h = await SeedAsync(0);
        var sentenceId = Guid.NewGuid();
        await using (var db = h.Db())
        {
            (await db.AnkiCollections.SingleAsync()).DefaultDirection = PracticeDirection.TargetToSource;
            db.QuizSentences.Add(new QuizSentence { Id = sentenceId, QuizId = h.QuizId, Text = "To jest dom.", Translation = "This is a house." });
            await db.SaveChangesAsync();
        }
        h.Model.ThenCall("add_anki_items", new { collection_id = CollectionId, kind = "sentences", item_ids = new[] { sentenceId.ToString() } })
            .Then(request =>
            {
                Assert.Equal(1, Output(request).GetProperty("anki_cards_added").GetInt32());
                return ScriptedModel.Reply("", ("add_anki_items", new { collection_id = CollectionId, kind = "sentences", item_ids = new[] { sentenceId.ToString() }, direction = "both" }));
            }).Then(request =>
            {
                Assert.Equal(1, Output(request).GetProperty("anki_cards_added").GetInt32());
                Assert.Equal(1, Output(request).GetProperty("anki_already_included").GetInt32());
                return ScriptedModel.Reply("Added.");
            });
        var run = await h.RunAsync("Add this sentence, then also the reverse card");
        Assert.Equal(AssistantRunStatus.Completed, run.Status);
        await using var after = h.Db();
        Assert.Equal(sentenceId, (await after.AnkiNotes.SingleAsync()).SentenceId);
        Assert.Equal(2, await after.AnkiCards.CountAsync());
        Assert.Equal(PracticeDirection.TargetToSource, (await after.AnkiCollections.SingleAsync()).DefaultDirection);
    }

    [Fact]
    public async Task Listing_preserves_alphabetical_default_and_created_order_is_stable_across_pages()
    {
        await using var h = await SeedAsync(205);
        await using var db = h.Db();
        var tools = AssistantToolFactory.Create(db);
        var context = new ToolContext { UserId = AssistantHarness.UserId, Mode = AssistantMode.Language, QuizId = h.QuizId };
        var alphabetic = JsonSerializer.SerializeToElement((await tools.RunAsync("list_items", "{\"kind\":\"words\"}", context)).Output);
        Assert.Equal(Id(204), alphabetic.GetProperty("words")[0].GetProperty("id").GetString());
        var first = JsonSerializer.SerializeToElement((await tools.RunAsync("list_items", "{\"kind\":\"words\",\"order\":\"created\"}", context)).Output);
        Assert.Equal(200, first.GetProperty("next_offset").GetInt32());
        var second = JsonSerializer.SerializeToElement((await tools.RunAsync("list_items", "{\"kind\":\"words\",\"order\":\"created\",\"offset\":200}", context)).Output);
        Assert.Equal(Enumerable.Range(0, 205).Select(Id), first.GetProperty("words").EnumerateArray().Concat(second.GetProperty("words").EnumerateArray()).Select(w => w.GetProperty("id").GetString()));
        Assert.False(second.GetProperty("has_more").GetBoolean());
        Assert.Null(second.GetProperty("next_offset").GetString());
    }

    [Fact]
    public Task Undo_of_create_add_and_link_reverses_all_changes_in_one_run() => VerifyChainedUndoAsync(null);

    [SqlServerFact]
    public Task SqlServer_undo_of_chained_additions_handles_generated_rowversions() =>
        SqlServerTestDatabase.RunAsync("assistant_anki", db => VerifyChainedUndoAsync(db.Database.GetConnectionString()));

    private static async Task VerifyChainedUndoAsync(string? connection)
    {
        await using var h = connection is null ? await AssistantHarness.CreateAsync() : await AssistantHarness.CreateSqlServerAsync(connection);
        await using (var db = h.Db())
        {
            db.Words.Add(new Word { Id = "chained", QuizId = h.QuizId, Lemma = "dom", Translation = "house" });
            await db.SaveChangesAsync();
        }
        Guid created = default;
        h.Model.ThenCall("create_anki_collection", new { name = "Chained" })
            .Then(request =>
            {
                created = Output(request).GetProperty("anki_collection_id").GetGuid();
                return ScriptedModel.Reply("", ("add_anki_items", new { collection_id = created, kind = "words", item_ids = new[] { "chained" } }));
            }).Then(_ => ScriptedModel.Reply("", ("link_anki_quiz", new { collection_id = created, content = "words", direction = "both" })))
            .ThenText("Created and linked.");
        var run = await h.RunAsync("Create a collection, add this word, then link the quiz in both directions");
        Assert.Equal(AssistantRunStatus.Completed, run.Status);
        var undone = await h.UndoAsync(run.Id);
        Assert.Equal(3, undone.Undone);
        Assert.Equal(0, undone.Kept);
        await using var after = h.Db();
        Assert.Empty(await after.AnkiCollections.ToListAsync());
        Assert.Empty(await after.AnkiNotes.ToListAsync());
        Assert.Empty(await after.AnkiCards.ToListAsync());
        Assert.Single(await after.Words.ToListAsync());
    }

    [Fact]
    public async Task Anki_tools_are_language_only_and_reject_invalid_batches_without_proposing_changes()
    {
        await using var h = await SeedAsync(1);
        await using var db = h.Db();
        var tools = AssistantToolFactory.Create(db);
        var context = new ToolContext { UserId = AssistantHarness.UserId, Mode = AssistantMode.Language, QuizId = h.QuizId };
        Assert.Equal(8, tools.Toolbox.Declarations(AssistantMode.Language).Count(t => t.Name.Contains("anki")));
        Assert.DoesNotContain(tools.Toolbox.Declarations(AssistantMode.Freestyle), t => t.Name.Contains("anki"));
        foreach (var ids in new[] { Array.Empty<string>(), Enumerable.Repeat(Id(0), 101).ToArray(), new[] { " " } })
        {
            var result = await tools.RunAsync("add_anki_items", JsonSerializer.Serialize(new { collection_id = CollectionId, kind = "words", item_ids = ids }), context);
            Assert.True(result.IsError);
            Assert.Empty(result.Changes);
        }
        var duplicate = await tools.RunAsync("create_anki_collection", "{\"name\":\"Revision\"}", context);
        Assert.True(duplicate.IsError);
        Assert.Empty(duplicate.Changes);
    }

    [Fact]
    public async Task Undo_keeps_a_new_collection_that_the_user_renamed()
    {
        await using var h = await SeedAsync(0);
        Guid created = default;
        h.Model.ThenCall("create_anki_collection", new { name = "Original" }).Then(request =>
        {
            created = Output(request).GetProperty("anki_collection_id").GetGuid();
            return ScriptedModel.Reply("Created.");
        });
        var run = await h.RunAsync("Create an Anki collection");
        await h.WithAsync(sp => sp.GetRequiredService<IAnkiCollectionService>().RenameAsync(created, "My edited name", AssistantHarness.UserId));
        Assert.Equal(1, (await h.UndoAsync(run.Id)).Kept);
        await using var after = h.Db();
        Assert.Equal("My edited name", (await after.AnkiCollections.SingleAsync(c => c.Id == created)).Name);
    }

    private static JsonElement Output(AgentRequest request)
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

    private static string Id(int i) => $"word-{i:D3}";
    private static AnkiCollection Collection(Guid id, string name) => new() { Id = id, Name = name, UserId = AssistantHarness.UserId,
        SourceLanguage = "English", TargetLanguage = "Polish", CreatedAt = Now, UpdatedAt = Now };
    private static Task<AssistantHarness> SeedAsync(int count) => AssistantHarness.CreateAsync(db =>
    {
        db.AnkiCollections.Add(Collection(CollectionId, "Revision"));
        for (var i = 0; i < count; i++) db.Words.Add(new Word { Id = Id(i), Lemma = $"word {999-i:D3}", Translation = $"translation {i}", CreatedAt = Now.AddMinutes(i / 2) });
    });
    private static Task<AnkiAdditionResult> AddAsync(AssistantHarness h, params string[] ids) =>
        h.WithAsync(sp => sp.GetRequiredService<IAnkiCollectionService>().AddItemsAsync(new(CollectionId, h.QuizId, PracticeItemType.Words, ids, true, false), AssistantHarness.UserId));
    private sealed class FailDirectCardSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AnkiCard>().Any(e => e.Entity.DirectlyIncluded && e.State == EntityState.Added))
                throw new InvalidOperationException("Injected card save failure");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
