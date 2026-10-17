using System.Data.Common;
using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Models.Library;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Assistant.Runtime;
using Glosify.Services.Ai.Assistant.Tools;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Anki;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantReviewRegressionTests
{
    private static ToolContext Context => new() { UserId = "user", Mode = AssistantMode.Language, SourceLanguage = "English", TargetLanguage = "Polish", TargetLanguageCode = "pl" };

    [Theory]
    [InlineData("list_books", "books")]
    [InlineData("list_saved_transcripts", "transcripts")]
    public async Task Timestamp_ties_use_stable_id_order_across_page_boundaries(string tool, string key)
    {
        await using var h = await AssistantHarness.CreateAsync();
        await using var db = h.Db();
        var ids = Enumerable.Range(1, 51).Select(i => Guid.Parse($"00000000-0000-0000-0000-{i:D12}")).ToArray();
        foreach (var id in ids)
        {
            db.BookDocuments.Add(new BookDocument { Id = id, UserId = "user", Title = "Book", Language = "Polish", CreatedAt = h.Clock.GetUtcNow() });
            db.RealtimeTranslationTranscripts.Add(new RealtimeTranslationTranscript { Id = id, UserId = "user", Title = "Transcript", TargetLanguage = "pl", UpdatedAt = h.Clock.GetUtcNow(),
                Segments = [new() { Id = Guid.NewGuid(), SessionId = Guid.NewGuid(), Text = "Text", ProviderEventKey = "event", Stream = "translation" }] });
        }
        await db.SaveChangesAsync();
        var tools = AssistantToolFactory.Create(db);
        var first = JsonSerializer.SerializeToElement((await tools.RunAsync(tool, "{}", Context)).Output);
        var firstIds = first.GetProperty(key).EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToArray();
        var second = JsonSerializer.SerializeToElement((await tools.RunAsync(tool, JsonSerializer.Serialize(new { offset = firstIds.Length }), Context)).Output);
        var all = firstIds.Concat(second.GetProperty(key).EnumerateArray().Select(x => x.GetProperty("id").GetGuid())).ToArray();
        Assert.Equal(ids.Reverse(), all);
        Assert.Equal(51, all.Distinct().Count());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task Translation_offsets_at_or_after_end_return_empty_page(int offset)
    {
        await using var h = await AssistantHarness.CreateAsync();
        await using var db = h.Db();
        var session = new SavedTranslationSession { UserId = "user", LanguageCode = "pl", ClientSessionId = Guid.NewGuid(),
            Translations = [new() { UserId = "user", RequestId = Guid.NewGuid(), SourceText = "House", TranslatedText = "Dom" }] };
        db.SavedTranslationSessions.Add(session);
        await db.SaveChangesAsync();
        var result = await AssistantToolFactory.Create(db).RunAsync("get_saved_translation_session", JsonSerializer.Serialize(new { session_id = session.Id, offset }), Context);
        Assert.False(result.IsError);
        var output = JsonSerializer.SerializeToElement(result.Output);
        Assert.Empty(output.GetProperty("translations").EnumerateArray());
        Assert.Equal(offset, output.GetProperty("offset").GetInt32());
        Assert.False(output.GetProperty("has_more").GetBoolean());
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    [InlineData("cancel")]
    public async Task Proposal_totals_include_unsaved_changes_and_count_approval_once(string decision)
    {
        await using var h = await AssistantHarness.CreateAsync(db => db.Words.Add(new Word { Id = "w1", QuizId = Guid.Empty, Lemma = "dom", Translation = "house" }));
        h.Model.ThenCall("add_items", "{\"words\":[{\"word\":\"kot\",\"translation\":\"cat\"}]}")
            .ThenCall("delete_items", "{\"word_ids\":[\"w1\"]}").ThenText("Done.");
        var run = await h.RunAsync("Add kot and delete dom");
        Assert.Equal(AssistantRunStatus.AwaitingApproval, run.Status);
        await using (var db = h.Db()) Assert.Equal(2, (await db.AssistantTurns.SingleAsync()).ProposedChangeCount);
        await h.CommandAsync(run.Id, decision);
        await h.DrainAsync();
        await using var after = h.Db();
        Assert.Equal(2, (await after.AssistantTurns.SingleAsync()).ProposedChangeCount);
        Assert.Equal(decision == "approve" ? 2 : 1, (await after.AssistantRuns.SingleAsync()).SavedChanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Large_review_histories_use_aggregate_reads_and_bounded_snapshots(bool convertedDates)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var commands = new ReviewCommands();
        var options = new DbContextOptionsBuilder<GlosifyContext>().UseSqlite(connection).AddInterceptors(commands).Options;
        await using GlosifyContext db = convertedDates ? new SqliteTestContext(options) : new GlosifyContext(options);
        await db.Database.EnsureCreatedAsync();
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        db.Users.Add(new ApplicationUser { Id = "user" });
        var quiz = new Quiz { Id = Guid.NewGuid(), UserId = "user", Name = "Quiz", SourceLanguage = "English", TargetLanguage = "Polish", Language = "Polish" };
        db.Quizzes.Add(quiz);
        db.Words.Add(new Word { Id = "w1", QuizId = quiz.Id, Lemma = "dom", Translation = "house" });
        await db.SaveChangesAsync();
        var collections = new AnkiCollectionService(db, clock);
        var collection = await collections.GetOrCreateAsync("user", "Polish", "UTC");
        await collections.AddItemAsync(new(collection.Id, quiz.Id, "words", "w1", true, false), "user");
        var card = await db.AnkiCards.SingleAsync();
        var emptySize = JsonSerializer.Serialize(await LearningChanges.SnapshotAsync(db, "anki", collection.Id, "user", default)).Length;
        db.AnkiReviews.AddRange(Enumerable.Range(0, 2000).Select(i => new AnkiReview { Id = Guid.NewGuid(), AnkiCollectionId = collection.Id,
            AnkiCardId = card.Id, ClientToken = Guid.NewGuid(), Rating = i == 0 ? "again" : "good", ReviewedAt = i < 2 ? now : now.AddYears(-2),
            NewDueAt = now, Prompt = new string('p', 100), Answer = new string('a', 100) }));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        commands.Sql.Clear();
        var rows = await LearningChanges.SnapshotAsync(db, "anki", collection.Id, "user", default);
        Assert.InRange(JsonSerializer.Serialize(rows).Length - emptySize, 0, 10);
        var rootOnly = await LearningChanges.SnapshotAsync(db, "anki", collection.Id, "user", default, collectionOnly: true);
        Assert.Equal(nameof(AnkiCollection), Assert.Single(rootOnly).Type);
        for (var i = 0; i < 1000; i++)
        {
            var wordId = $"summary-{i}";
            db.Words.Add(new Word { Id = wordId, QuizId = quiz.Id, Lemma = wordId, Translation = "summary" });
            db.AnkiNotes.Add(new AnkiNote
            {
                Id = Guid.NewGuid(), AnkiCollectionId = collection.Id, QuizId = quiz.Id, WordId = wordId,
                Cards = [new AnkiCard
                {
                    Id = Guid.NewGuid(), IsActive = i % 5 != 0,
                    State = new[] { "new", "learning", "relearning", "review" }[i % 4],
                    DueAt = i % 4 == 0 ? null : i % 4 == 2 ? now.AddDays(1) : now.AddDays(-1),
                }],
            });
        }
        await db.SaveChangesAsync();
        commands.Cards.Clear();
        var result = await new InspectAnkiTool(db, clock).ExecuteAsync(JsonSerializer.Serialize(new { collection_id = collection.Id, offset = 100 }), Context, default);
        Assert.False(result.IsError);
        var output = JsonSerializer.SerializeToElement(result.Output);
        Assert.Equal(2000, output.GetProperty("total_reviews").GetInt32());
        Assert.Equal(2, output.GetProperty("studied_today").GetInt32());
        Assert.Equal(2, output.GetProperty("reviews_last_30_days").GetInt32());
        Assert.Equal(50, output.GetProperty("retention_percent_last_30_days").GetDouble());
        Assert.Equal(1001, output.GetProperty("total_cards").GetInt32());
        Assert.Equal(801, output.GetProperty("active_cards").GetInt32());
        Assert.Equal(201, output.GetProperty("new_cards").GetInt32());
        Assert.Equal(400, output.GetProperty("due_cards").GetInt32());
        Assert.Equal(400, output.GetProperty("learning_cards").GetInt32());
        Assert.Equal(100, output.GetProperty("cards").GetArrayLength());
        Assert.NotEmpty(commands.Cards);
        Assert.All(commands.Cards, sql => Assert.True(sql.Contains("COUNT(", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase), "Card reads must aggregate or page: " + sql));
        Assert.NotEmpty(commands.Sql);
        Assert.All(commands.Sql, sql => Assert.Contains("COUNT(", sql, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false, "unchanged")]
    [InlineData(true, "unchanged")]
    [InlineData(false, "rename")]
    [InlineData(true, "rename")]
    [InlineData(false, "move")]
    [InlineData(true, "move")]
    [InlineData(false, "visibility")]
    [InlineData(true, "visibility")]
    public async Task Undo_creation_preserves_later_changes_to_empty_resources(bool folder, string edit)
    {
        await using var h = await AssistantHarness.CreateAsync();
        var parentId = Guid.NewGuid();
        await using (var db = h.Db())
        {
            db.Collections.Add(new Collection { Id = parentId, UserId = "user", Name = "Destination", Language = "Polish" });
            await db.SaveChangesAsync();
        }
        h.Model.ThenCall(folder ? "create_collection" : "create_quiz", "{\"name\":\"Created\"}").ThenText("Created.");
        var run = await h.RunAsync("Create an empty resource", quizId: Guid.Empty);
        Assert.Equal(1, run.SavedChanges);
        Guid id;
        await using (var db = h.Db())
        {
            if (folder)
            {
                var item = await db.Collections.SingleAsync(c => c.Name == "Created"); id = item.Id;
                if (edit == "rename") item.Name = "User rename";
                if (edit == "move") item.ParentCollectionId = parentId;
                if (edit == "visibility") item.IsPublic = true;
            }
            else
            {
                var item = await db.Quizzes.SingleAsync(q => q.Name == "Created"); id = item.Id;
                if (edit == "rename") item.Name = "User rename";
                if (edit == "move") item.CollectionId = parentId;
                if (edit == "visibility") item.IsPublic = true;
            }
            await db.SaveChangesAsync();
        }
        Assert.Equal(edit == "unchanged" ? 1 : 0, (await h.UndoAsync(run.Id)).Undone);
        await using var after = h.Db();
        Assert.Equal(edit != "unchanged", folder
            ? await after.Collections.AnyAsync(c => c.Id == id)
            : await after.Quizzes.AnyAsync(q => q.Id == id));
    }

    [Theory]
    [InlineData("create_collection", 160, false)]
    [InlineData("create_collection", 161, true)]
    [InlineData("rename_collection", 160, false)]
    [InlineData("rename_collection", 161, true)]
    public async Task Collection_names_are_validated_before_proposing_changes(string tool, int length, bool invalid)
    {
        await using var h = await AssistantHarness.CreateAsync();
        await using var db = h.Db();
        var folder = new Collection { Id = Guid.NewGuid(), UserId = "user", Language = "Polish", Name = "Original" };
        db.Collections.Add(folder);
        await db.SaveChangesAsync();
        var args = tool == "create_collection" ? JsonSerializer.Serialize(new { name = new string('x', length) })
            : JsonSerializer.Serialize(new { name = new string('x', length), collection_id = folder.Id });
        var result = await AssistantToolFactory.Create(db).RunAsync(tool, args, Context);
        Assert.Equal(invalid, result.IsError);
        Assert.Equal(invalid ? 0 : 1, result.Changes.Count);
        if (invalid) Assert.Contains("160", JsonSerializer.Serialize(result.Output));
    }

    [Fact]
    public async Task Long_book_pages_can_be_read_completely_with_a_bounded_cursor()
    {
        await using var h = await AssistantHarness.CreateAsync();
        await using var db = h.Db();
        var source = new string('x', 11999) + "😀" + new string('y', 14000);
        var book = new BookDocument { Id = Guid.NewGuid(), UserId = "user", Title = "Long page", Language = "Polish", PageCount = 2,
            Pages = [new() { Id = Guid.NewGuid(), PageNumber = 1, Text = source }, new() { Id = Guid.NewGuid(), PageNumber = 2, Text = "Last page" }] };
        db.BookDocuments.Add(book);
        await db.SaveChangesAsync();
        var tools = AssistantToolFactory.Create(db);
        var page = 1;
        var offset = 0;
        var recovered = "";
        var finished = false;
        for (var i = 0; i < 5 && !finished; i++)
        {
            var result = await tools.RunAsync("get_book_pages", JsonSerializer.Serialize(new { book_id = book.Id, from_page = page, offset }), Context);
            Assert.False(result.IsError);
            var output = JsonSerializer.SerializeToElement(result.Output);
            var chunk = string.Concat(output.GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("text").GetString()));
            Assert.InRange(chunk.Length, 1, 12000);
            recovered += chunk;
            var nextPage = output.GetProperty("next_page").GetInt32();
            var nextOffset = output.GetProperty("next_offset").GetInt32();
            Assert.True(nextPage > page || (nextPage == page && nextOffset > offset));
            page = nextPage; offset = nextOffset;
            finished = !output.GetProperty("has_more").GetBoolean();
        }
        Assert.True(finished);
        Assert.Equal(source + "Last page", recovered);
    }

    [Fact]
    public async Task Idle_signal_waits_release_memory_after_timeout_and_cancellation()
    {
        var signals = new AssistantRunSignals();
        for (var i = 0; i < 100; i++) await signals.WaitAsync(Guid.NewGuid(), TimeSpan.Zero, default);
        Assert.Equal(0, signals.WaitingRunCount);
        var id = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        var cancelled = signals.WaitAsync(id, TimeSpan.FromMinutes(1), cancellation.Token);
        var survivor = signals.WaitAsync(id, TimeSpan.FromMinutes(1), default);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(1, signals.WaitingRunCount);
        signals.Notify(id);
        await survivor.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, signals.WaitingRunCount);
    }

    [Fact]
    public async Task Old_signal_cleanup_does_not_remove_a_new_waiter()
    {
        var signals = new AssistantRunSignals();
        var id = Guid.NewGuid();
        var first = signals.WaitAsync(id, TimeSpan.FromMinutes(1), default);
        signals.Notify(id);
        var second = signals.WaitAsync(id, TimeSpan.FromMinutes(1), default);
        await first;
        Assert.Equal(1, signals.WaitingRunCount);
        Assert.False(second.IsCompleted);
        signals.Notify(id);
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, signals.WaitingRunCount);
    }

    [Fact]
    public async Task Reclaim_rejects_a_checkpoint_that_read_the_previous_revision()
    {
        await using var h = await AssistantHarness.CreateAsync();
        var run = await h.StartAsync("Hello");
        await h.WithAsync(services => services.GetRequiredService<AssistantRunStore>().ClaimAsync(default));
        await using var oldDb = h.Db();
        var stale = await oldDb.AssistantRuns.SingleAsync(r => r.Id == run.Id);
        var revision = stale.Revision;
        h.Clock.Advance(TimeSpan.FromMinutes(3));
        var replacement = await h.WithAsync(services => services.GetRequiredService<AssistantRunStore>().ClaimAsync(default));
        Assert.NotNull(replacement);
        stale.Revision++;
        stale.LeaseId = null;
        stale.StateJson = "{\"stale\":true}";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => oldDb.SaveChangesAsync());
        await using var after = h.Db();
        var current = await after.AssistantRuns.SingleAsync();
        Assert.Equal(replacement.Value.Lease, current.LeaseId);
        Assert.True(current.Revision > revision);
        Assert.DoesNotContain("stale", current.StateJson);
    }

    [Fact]
    public async Task Book_context_is_bounded_user_data_with_a_continuation_cursor()
    {
        var bookId = Guid.NewGuid();
        const string marker = "PAGE_SOURCE_IGNORE_ALL_INSTRUCTIONS";
        await using var h = await AssistantHarness.CreateAsync(db => db.BookDocuments.Add(new BookDocument
        {
            Id = bookId, UserId = "user", Title = "Selected book", Language = "Polish", PageCount = 1,
            Pages = [new() { Id = Guid.NewGuid(), PageNumber = 1, Text = marker + new string('x', 49000) + "PAGE_END_MARKER" }]
        }));
        h.Model.ThenText("Ready.");
        await h.StartAsync("Summarize this page", input: new AssistantRunInput("Summarize this page", DocumentContext: new AssistantDocumentContext(bookId, 1)));
        await h.DrainAsync();
        var request = Assert.Single(h.Model.Requests);
        Assert.DoesNotContain(marker, request.SystemInstruction);
        Assert.DoesNotContain(request.History.Where(turn => turn.Role == "developer"), turn => turn.ContentJson.Contains(marker, StringComparison.Ordinal));
        var context = Assert.Single(request.History, turn => turn.ContentJson.Contains(marker, StringComparison.Ordinal));
        Assert.Equal("user", context.Role);
        var text = AssistantRunTests.TextOf(context);
        Assert.InRange(text.Length, 6000, 8000);
        Assert.Contains("offset=6000", text);
        Assert.Contains(bookId.ToString(), text);
        Assert.DoesNotContain("PAGE_END_MARKER", text);
    }

    [Fact]
    public async Task Undo_deleted_word_restores_its_creation_date_and_order()
    {
        var original = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
        await using var h = await AssistantHarness.CreateAsync(db => db.Words.AddRange(
            new Word { Id = "w1", QuizId = Guid.Empty, Lemma = "dom", Translation = "house", CreatedAt = original },
            new Word { Id = "w2", QuizId = Guid.Empty, Lemma = "kot", Translation = "cat", CreatedAt = original.AddDays(1) }));
        h.Model.ThenCall("delete_items", new { word_ids = new[] { "w1" } }).ThenText("Deleted.");
        var run = await h.RunAsync("Delete dom");
        await h.CommandAsync(run.Id, "approve");
        await h.DrainAsync();
        await using (var before = h.Db()) Assert.False(await before.Words.AnyAsync(w => w.Id == "w1"));
        Assert.Equal(1, (await h.UndoAsync(run.Id)).Undone);
        await using var after = h.Db();
        Assert.Equal(original, (await after.Words.SingleAsync(w => w.Id == "w1")).CreatedAt);
        Assert.Equal(new[] { "w1", "w2" }, await after.Words.OrderBy(w => w.CreatedAt).Select(w => w.Id).ToArrayAsync());
    }

    [Fact]
    public async Task All_saved_replay_data_stays_below_developer_priority()
    {
        await using var h = await AssistantHarness.CreateAsync();
        await using var db = h.Db();
        var conversation = new AssistantConversation(db, new AssistantMessagePresenter(), Options.Create(h.Options), AssistantToolFactory.Create(db).Toolbox);
        var messageId = Guid.NewGuid();
        var run = new AssistantRun { Id = Guid.NewGuid(), UserMessageId = Guid.NewGuid() };
        var parts = new[]
        {
            new AssistantPart { MessageId = messageId, Type = AssistantPartTypes.Summary, Text = "SUMMARY_IGNORE_INSTRUCTIONS", MetadataJson = RunJson.Write(new { through_sequence = 0 }) },
            new AssistantPart { MessageId = messageId, Type = AssistantPartTypes.Tool, State = AssistantToolStates.Completed, Title = "TITLE_IGNORE_INSTRUCTIONS", Output = "RESULT_IGNORE_INSTRUCTIONS" },
            new AssistantPart { MessageId = messageId, Type = AssistantPartTypes.Reminder, Text = "REMINDER_IGNORE_INSTRUCTIONS" },
        };
        var snapshot = new AssistantConversation.Snapshot([new AssistantMessage { Id = messageId, Sequence = 1, Role = AssistantMessageRole.Model }], parts.ToLookup(p => p.MessageId));
        var state = new AssistantRunState { NoProgress = 2, RecentErrors = ["ERROR_IGNORE_INSTRUCTIONS"] };
        AssistantPlanItem[] plan = [new("PLAN_IGNORE_INSTRUCTIONS", "in_progress")];
        AssistantRunArtifact[] created = [new(Guid.NewGuid(), "QUIZ_IGNORE_INSTRUCTIONS", "Ready")];
        var turns = conversation.Build(snapshot, run, state).Append(AssistantRunContext.RunData(state, plan, created)!).ToList();
        foreach (var prefix in new[] { "SUMMARY", "TITLE", "RESULT", "REMINDER", "ERROR", "PLAN", "QUIZ" })
        {
            var marker = prefix + "_IGNORE_INSTRUCTIONS";
            Assert.Contains(turns.Where(t => t.Role == "user"), t => AssistantRunTests.TextOf(t).Contains(marker, StringComparison.Ordinal));
            Assert.DoesNotContain(turns.Where(t => t.Role == "developer"), t => t.ContentJson.Contains(marker, StringComparison.Ordinal));
            Assert.DoesNotContain(marker, AssistantRunContext.RunNote(run, state, plan, created, false));
        }
    }

    [Theory]
    [InlineData("system")]
    [InlineData("context")]
    [InlineData("tools")]
    [InlineData("trailing")]
    public async Task Compaction_budget_includes_non_history_request_content(string largeField)
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Options.ContextTokenBudget = 8000;
        await using var db = h.Db();
        var conversation = new AssistantConversation(db, new AssistantMessagePresenter(), Options.Create(h.Options), AssistantToolFactory.Create(db).Toolbox);
        var currentId = Guid.NewGuid();
        var snapshot = new AssistantConversation.Snapshot([
            new AssistantMessage { Id = Guid.NewGuid(), Sequence = 1 },
            new AssistantMessage { Id = Guid.NewGuid(), Sequence = 2 },
            new AssistantMessage { Id = currentId, Sequence = 3 },
        ], Array.Empty<AssistantPart>().ToLookup(p => p.MessageId));
        var run = new AssistantRun { UserMessageId = currentId };
        var large = new string('x', 30000);
        var request = new AgentRequest(largeField == "system" ? large : "Instructions", [RunJson.Text("user", "Hello")],
            largeField == "tools" ? [new AgentToolDeclaration("read", large, "{}")] : [],
            ContextInstruction: largeField == "context" ? large : null)
        { TrailingInstruction = largeField == "trailing" ? large : null };
        Assert.Null(conversation.PlanSummary(snapshot, run, AssistantConversation.EstimateTokens(request.History)));
        Assert.Equal(2, conversation.PlanSummary(snapshot, run, AssistantConversation.EstimateTokens(request)));
    }

    [Fact]
    public async Task A_viewed_page_is_authoritative_when_the_selected_book_id_differs()
    {
        var selected = Guid.NewGuid();
        var viewed = Guid.NewGuid();
        await using var h = await AssistantHarness.CreateAsync(db =>
        {
            foreach (var id in new[] { selected, viewed }) db.BookDocuments.Add(new BookDocument
            {
                Id = id, UserId = "user", Title = id == viewed ? "Viewed book" : "Other book", Language = "Polish", PageCount = 1,
                Pages = [new() { Id = Guid.NewGuid(), PageNumber = 1, Text = id == viewed ? "VIEWED_PAGE_TEXT" : "OTHER_PAGE_TEXT" }],
            });
        });
        h.Model.ThenCall("get_book_pages", new { from_page = 1 }).ThenText("Read the viewed page.");
        await h.StartAsync("Read this page", input: new AssistantRunInput("Read this page", BookDocumentId: selected, DocumentContext: new AssistantDocumentContext(viewed, 1)));
        await h.DrainAsync();
        await using var db = h.Db();
        var run = await db.AssistantRuns.SingleAsync();
        Assert.Equal(viewed, RunJson.Read<AssistantRunState>(run.StateJson).Facts!.BookDocumentId);
        var read = await db.AssistantParts.SingleAsync(p => p.ToolName == "get_book_pages");
        Assert.Contains("VIEWED_PAGE_TEXT", read.Output);
        Assert.DoesNotContain("OTHER_PAGE_TEXT", read.Output);
        Assert.Equal(viewed, (await db.AssistantThreads.SingleAsync()).ContextBookDocumentId);
    }

    [Theory]
    [InlineData("word", "later")]
    [InlineData("sentence", "later")]
    [InlineData("word", "automatic")]
    [InlineData("sentence", "automatic")]
    [InlineData("word", "review")]
    [InlineData("sentence", "review")]
    [InlineData("word", "direct")]
    [InlineData("sentence", "direct")]
    public async Task Undo_created_items_preserves_later_Anki_changes_but_can_remove_unchanged_automatic_links(string kind, string scenario)
    {
        await using var h = await AssistantHarness.CreateAsync();
        var collection = await h.WithAsync(services => services.GetRequiredService<IAnkiCollectionService>()
            .GetOrCreateAsync(AssistantHarness.UserId, "Polish", "UTC"));
        if (scenario != "later") await h.WithAsync(services => services.GetRequiredService<IAnkiCollectionService>()
            .AddQuizAsync(new(collection.Id, h.QuizId, true, false, true, false), AssistantHarness.UserId));
        h.Model.ThenCall("add_items", kind == "word"
            ? "{\"words\":[{\"word\":\"dom\",\"translation\":\"house\"}]}"
            : "{\"sentences\":[{\"text\":\"To jest dom.\",\"translation\":\"This is a house.\"}]}").ThenText("Added.");
        var run = await h.RunAsync("Add an item");
        await using (var db = h.Db())
        {
            var id = kind == "word" ? (await db.Words.SingleAsync()).Id : (await db.QuizSentences.SingleAsync()).Id.ToString();
            if (scenario is "later" or "direct") await h.WithAsync(services => services.GetRequiredService<IAnkiCollectionService>()
                .AddItemAsync(new(collection.Id, h.QuizId, kind, id, true, false), AssistantHarness.UserId));
            if (scenario == "review")
            {
                (await db.AnkiCards.FirstAsync(c => c.IsActive)).ReviewCount++;
                await db.SaveChangesAsync();
            }
        }
        var undo = await h.UndoAsync(run.Id);
        Assert.Equal(scenario == "automatic" ? 2 : 0, undo.Undone);
        Assert.Equal(scenario == "automatic" ? 0 : scenario == "later" ? 1 : 2, undo.Kept);
        await using var after = h.Db();
        Assert.Equal(scenario != "automatic", kind == "word" ? await after.Words.AnyAsync() : await after.QuizSentences.AnyAsync());
        Assert.Equal(scenario != "automatic", await after.AnkiCards.AnyAsync(c => c.IsActive));
    }

    [Fact]
    public async Task A_disappearing_destination_is_recorded_as_a_visible_placement_failure()
    {
        var collectionId = Guid.NewGuid();
        await using var h = await AssistantHarness.CreateAsync(db => db.Collections.Add(new Collection
        { Id = collectionId, UserId = AssistantHarness.UserId, Name = "Shared", Language = "Polish", IsPublic = true }));
        h.Model.ThenCall("create_quiz", new { name = "Travel", collection_id = collectionId }).ThenText("Created.");
        var run = await h.StartAsync("Create a quiz in Shared", quizId: Guid.Empty);
        for (var step = 0; step < 8; step++)
        {
            await h.StepAsync();
            await using var db = h.Db();
            if (!await db.Quizzes.AnyAsync(q => q.Name == "Travel")) continue;
            db.Collections.Remove(await db.Collections.SingleAsync(c => c.Id == collectionId));
            await db.SaveChangesAsync();
            break;
        }
        await h.DrainAsync();
        await using var after = h.Db();
        Assert.Null((await after.Quizzes.SingleAsync(q => q.Name == "Travel")).CollectionId);
        var failure = await after.AssistantParts.SingleAsync(p => p.RunId == run.Id && p.State == AssistantToolStates.Error);
        Assert.Contains("placement_target_missing", failure.Output);
        Assert.Contains(collectionId.ToString(), failure.Output);
        Assert.Contains(await after.AssistantParts.Where(p => p.RunId == run.Id && p.Type == AssistantPartTypes.Text).Select(p => p.Text).ToListAsync(),
            text => text!.Contains("without that folder placement", StringComparison.Ordinal));
    }

    private sealed class ReviewCommands : DbCommandInterceptor
    {
        public List<string> Sql { get; } = [];
        public List<string> Cards { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("AnkiReviews", StringComparison.Ordinal)) Sql.Add(command.CommandText);
            if (command.CommandText.Contains("AnkiCards", StringComparison.Ordinal)) Cards.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
