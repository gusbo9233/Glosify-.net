using System.Globalization;
using Glosify.Data;
using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Services.Anki;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Glosify.Tests;

public sealed class AnkiCollectionServiceTests
{
    private const int FormerDailyNewLimit = 20;
    private const string UserId = "anki-user";
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Get_or_create_returns_one_collection_per_learning_language()
    {
        await using var f = await Fixture.CreateAsync();

        var first = await f.Collections.GetOrCreateAsync(UserId, "pl", "Europe/Stockholm");
        var again = await f.Collections.GetOrCreateAsync(UserId, "Polish", null);
        var spanish = await f.Collections.GetOrCreateAsync(UserId, "Spanish", "UTC");

        Assert.Equal(first.Id, again.Id);
        Assert.NotEqual(first.Id, spanish.Id);
        Assert.Equal("Polish", first.TargetLanguage);
        // A missing zone keeps the stored one; a reported zone replaces it.
        Assert.Equal("Europe/Stockholm", again.TimeZoneId);
        Assert.Equal("UTC", (await f.Collections.GetOrCreateAsync(UserId, "Polish", "UTC")).TimeZoneId);
        Assert.Equal(2, await f.Context.AnkiCollections.CountAsync());
        Assert.Equal(first.Id, (await f.Collections.FindForLanguageAsync(UserId, "POLISH"))!.Id);
        Assert.Null(await f.Collections.FindForLanguageAsync("someone-else", "Polish"));
        await Assert.ThrowsAsync<AnkiValidationException>(() => f.Collections.GetOrCreateAsync(UserId, "Klingon", null));
    }

    [Fact]
    public async Task Database_rejects_a_second_collection_for_the_same_language()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Collections.GetOrCreateAsync(UserId, "Polish", null);
        f.Context.AnkiCollections.Add(new AnkiCollection { Id = Guid.NewGuid(), UserId = UserId, Name = "Duplicate", TargetLanguage = "Polish" });

        await Assert.ThrowsAsync<DbUpdateException>(() => f.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task Get_or_create_returns_the_winner_when_a_concurrent_request_creates_it_first()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var plain = new DbContextOptionsBuilder<GlosifyContext>().UseSqlite(connection).Options;
        await using (var setup = new GlosifyContext(plain))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Users.Add(new ApplicationUser { Id = UserId, UserName = "anki@example.test", NormalizedUserName = "ANKI@EXAMPLE.TEST" });
            await setup.SaveChangesAsync();
        }
        var competitorId = Guid.NewGuid();
        var race = new CompetingCollectionInterceptor(plain, competitorId);
        await using var context = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>()
            .UseSqlite(connection).AddInterceptors(race).Options);

        var collection = await new AnkiCollectionService(context, new FakeTimeProvider(Now))
            .GetOrCreateAsync(UserId, "Polish", "UTC");

        Assert.True(race.Fired);
        Assert.Equal(competitorId, collection.Id);
        Assert.Equal(competitorId, (await context.AnkiCollections.AsNoTracking().SingleAsync()).Id);
    }

    [Fact]
    public async Task Practice_direction_is_a_preference_and_never_changes_membership()
    {
        await using var f = await Fixture.CreateAsync();
        var collection = await f.Collections.GetOrCreateAsync(UserId, "Polish", "UTC");
        await f.Collections.AddQuizAsync(new(collection.Id, f.Quiz.Id, true, true, false, false), UserId);
        var before = await f.Context.AnkiCards.AsNoTracking().OrderBy(c => c.Id).Select(c => new { c.Id, c.IsActive }).ToListAsync();

        Assert.False(await f.Collections.SetPracticeDirectionAsync(collection.Id, PracticeDirection.TargetToSource, "someone-else"));
        Assert.True(await f.Collections.SetPracticeDirectionAsync(collection.Id, PracticeDirection.TargetToSource, UserId));
        await Assert.ThrowsAsync<AnkiValidationException>(() =>
            f.Collections.SetPracticeDirectionAsync(collection.Id, "sideways", UserId));
        await f.Collections.SyncCollectionAsync(collection.Id);

        Assert.Equal(PracticeDirection.TargetToSource, (await f.Context.AnkiCollections.AsNoTracking().SingleAsync()).DefaultDirection);
        var after = await f.Context.AnkiCards.AsNoTracking().OrderBy(c => c.Id).Select(c => new { c.Id, c.IsActive }).ToListAsync();
        Assert.Equal(before, after);
        Assert.Equal(4, after.Count(c => c.IsActive));
    }

    [Fact]
    public async Task Notes_from_different_source_languages_share_the_collection_with_their_own_labels()
    {
        await using var f = await Fixture.CreateAsync();
        var swedishQuiz = new Quiz { Id = Guid.NewGuid(), UserId = UserId, Name = "Från svenska", SourceLanguage = "Swedish", TargetLanguage = "Polish", Language = "Polish", ProcessingStatus = "Ready", CreatedAt = Now };
        var swedishWord = new Word { Id = "word-sv", QuizId = swedishQuiz.Id, Lemma = "pies", Translation = "hund", CreatedAt = Now };
        f.Context.AddRange(swedishQuiz, swedishWord);
        await f.Context.SaveChangesAsync();
        var collection = await f.Collections.GetOrCreateAsync(UserId, "Polish", "UTC");

        Assert.True(await f.Collections.AddItemAsync(new(collection.Id, f.Quiz.Id, "words", f.Word.Id, true, true), UserId));
        Assert.True(await f.Collections.AddItemAsync(new(collection.Id, swedishQuiz.Id, "words", swedishWord.Id, true, true), UserId));

        var notes = await f.Context.AnkiNotes.AsNoTracking().ToListAsync();
        Assert.Equal("English", notes.Single(n => n.WordId == f.Word.Id).SourceLanguage);
        Assert.Equal("Swedish", notes.Single(n => n.WordId == swedishWord.Id).SourceLanguage);

        var prompts = new List<(string Prompt, string Language)>();
        for (var i = 0; i < 2; i++)
        {
            var card = (await f.Study.GetNextAsync(collection.Id, UserId, PracticeDirection.SourceToTarget))!.Card!;
            prompts.Add((card.Prompt, card.PromptLanguage));
            Assert.Equal("Polish", card.AnswerLanguage);
            Assert.True(await f.Study.RateAsync(new(collection.Id, card.CardId, AnkiRatings.Easy, Guid.NewGuid(), card.RowVersion, 100), UserId));
        }
        Assert.Contains(("house", "English"), prompts);
        Assert.Contains(("hund", "Swedish"), prompts);
    }

    [Theory]
    [InlineData(AnkiCardStates.New)]
    [InlineData(AnkiCardStates.Review)]
    public async Task Again_rejoins_the_back_of_the_stack_immediately_and_survives_reload(string state)
    {
        await using var f = await Fixture.CreateAsync();
        var collection = await f.Collections.GetOrCreateAsync(UserId, "Polish", "UTC");
        await f.Collections.AddQuizAsync(new(collection.Id, f.Quiz.Id, true, false, false, false), UserId);
        foreach (var card in await f.Context.AnkiCards.ToListAsync())
        {
            card.State = state;
            card.DueAt = Now.AddDays(-1);
        }
        await f.Context.SaveChangesAsync();
        var first = (await f.Study.GetNextAsync(collection.Id, UserId, PracticeDirection.SourceToTarget))!.Card!;

        Assert.True(await f.Study.RateAsync(new(collection.Id, first.CardId, AnkiRatings.Again, Guid.NewGuid(), first.RowVersion, 100), UserId));
        var second = (await f.Study.GetNextAsync(collection.Id, UserId, PracticeDirection.SourceToTarget))!.Card!;
        Assert.NotEqual(first.CardId, second.CardId);
        Assert.Equal(1, (await f.Collections.ReadCountsAsync(collection.Id, UserId))!.Learning);
        Assert.True(await f.Study.RateAsync(new(collection.Id, second.CardId, AnkiRatings.Easy, Guid.NewGuid(), second.RowVersion, 100), UserId));

        f.Context.ChangeTracker.Clear();
        var reloaded = new AnkiStudyService(f.Context, f.Collections, new Fsrs6AnkiScheduler(), new FakeTimeProvider(Now));
        var repeated = (await reloaded.GetNextAsync(collection.Id, UserId, PracticeDirection.SourceToTarget))!.Card;
        Assert.NotNull(repeated);
        Assert.Equal(first.CardId, repeated.CardId);
        // No clock advance, and repeated Again still keeps the only remaining card available.
        Assert.True(await reloaded.RateAsync(new(collection.Id, repeated.CardId, AnkiRatings.Again, Guid.NewGuid(), repeated.RowVersion, 100), UserId));
        Assert.Equal(first.CardId, (await reloaded.GetNextAsync(collection.Id, UserId, PracticeDirection.SourceToTarget))!.Card?.CardId);
        Assert.Equal(3, await f.Context.AnkiReviews.CountAsync());
        Assert.All(await f.Context.AnkiReviews.Where(r => r.Rating == AnkiRatings.Again).ToListAsync(), review =>
            Assert.Equal(review.ReviewedAt, review.NewDueAt));
    }

    [Fact]
    public async Task Multiple_again_cards_rotate_in_review_order()
    {
        await using var f = await Fixture.CreateAsync();
        var collection = await f.Collections.GetOrCreateAsync(UserId, "Polish", "UTC");
        await f.Collections.AddQuizAsync(new(collection.Id, f.Quiz.Id, true, false, false, false), UserId);
        var clock = new FakeTimeProvider(Now);
        var study = new AnkiStudyService(f.Context, f.Collections, new Fsrs6AnkiScheduler(), clock);
        var ids = new List<Guid>();

        for (var i = 0; i < 4; i++)
        {
            var card = (await study.GetNextAsync(collection.Id, UserId, PracticeDirection.SourceToTarget))!.Card!;
            ids.Add(card.CardId);
            Assert.True(await study.RateAsync(new(collection.Id, card.CardId, AnkiRatings.Again, Guid.NewGuid(), card.RowVersion, 100), UserId));
            clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.NotEqual(ids[0], ids[1]);
        Assert.Equal(ids[0], ids[2]);
        Assert.Equal(ids[1], ids[3]);
    }

    [Fact]
    public async Task Again_remains_available_after_all_new_cards_are_studied()
    {
        await using var f = await Fixture.CreateAsync();
        var collection = await f.Collections.GetOrCreateAsync(UserId, "Polish", "UTC");
        await AddMoreThanADaysNewCardsAsync(f, collection.Id);
        Guid missedId = default;
        for (var i = 0; i <= FormerDailyNewLimit; i++)
        {
            var card = (await f.Study.GetNextAsync(collection.Id, UserId, PracticeDirection.SourceToTarget))!.Card!;
            var rating = i == 0 ? AnkiRatings.Again : AnkiRatings.Easy;
            if (i == 0) missedId = card.CardId;
            Assert.True(await f.Study.RateAsync(new(collection.Id, card.CardId, rating, Guid.NewGuid(), card.RowVersion, 100), UserId));
        }

        var repeated = (await f.Study.GetNextAsync(collection.Id, UserId, PracticeDirection.SourceToTarget))!.Card!;
        Assert.Equal(missedId, repeated.CardId);
        Assert.Equal(0, repeated.NewRemaining);
        Assert.True(await f.Study.RateAsync(new(collection.Id, repeated.CardId, AnkiRatings.Easy, Guid.NewGuid(), repeated.RowVersion, 100), UserId));
        Assert.Null((await f.Study.GetNextAsync(collection.Id, UserId, PracticeDirection.SourceToTarget))!.Card);
        Assert.Equal(0, await f.Context.AnkiCards.CountAsync(card => card.Direction == PracticeDirection.SourceToTarget && card.State == AnkiCardStates.New));
    }

    [Theory]
    [InlineData(AnkiCardStates.New)]
    [InlineData(AnkiCardStates.Review)]
    public async Task Study_and_suggestions_use_shared_defaults_despite_legacy_collection_settings(string state)
    {
        await using var f = await Fixture.CreateAsync();
        var collection = await f.Collections.GetOrCreateAsync(UserId, "Polish", "UTC");
        collection.DesiredRetention = 0.7;
        collection.NewCardsPerDay = 0;
        collection.MaximumReviewsPerDay = 0;
        await f.Context.SaveChangesAsync();
        await f.Collections.AddItemAsync(new(collection.Id, f.Quiz.Id, "words", f.Word.Id, true, false), UserId);
        var stored = await f.Context.AnkiCards.SingleAsync();
        stored.State = state;
        stored.DueAt = Now;
        stored.Stability = 10;
        stored.Difficulty = 5;
        stored.LastReviewedAt = Now.AddDays(-10);
        await f.Context.SaveChangesAsync();
        var scheduler = new Fsrs6AnkiScheduler();
        var expected = scheduler.Schedule(stored, AnkiRatings.Good, AnkiStudyDefaults.DesiredRetention, Now);

        var counts = await f.Collections.ReadCountsAsync(collection.Id, UserId);
        Assert.Equal(state == AnkiCardStates.New ? 1 : 0, counts!.New);
        Assert.Equal(state == AnkiCardStates.Review ? 1 : 0, counts.Due);
        var next = (await f.Study.GetNextAsync(collection.Id, UserId, PracticeDirection.SourceToTarget))!.Card;
        Assert.NotNull(next);
        Assert.Equal(expected.IntervalLabel, next.Intervals[AnkiRatings.Good]);
        Assert.True(await f.Study.RateAsync(new(collection.Id, next.CardId, AnkiRatings.Good, Guid.NewGuid(), next.RowVersion, 100), UserId));
        Assert.Equal(expected.DueAt, (await f.Context.AnkiCards.SingleAsync()).DueAt);
    }

    [Fact]
    public async Task Directions_are_separate_tracks_without_daily_limits()
    {
        await using var f = await Fixture.CreateAsync();
        var collection = await f.Collections.GetOrCreateAsync(UserId, "Polish", "UTC");
        await AddMoreThanADaysNewCardsAsync(f, collection.Id);

        var forward = (await f.Study.GetNextAsync(collection.Id, UserId, PracticeDirection.SourceToTarget))!.Card!;
        Assert.Equal(PracticeDirection.SourceToTarget, forward.Direction);
        Assert.Contains(forward.Prompt, await f.Context.Words.Select(word => word.Translation).ToListAsync());
        Assert.True(await f.Study.RateAsync(new(collection.Id, forward.CardId, AnkiRatings.Easy, Guid.NewGuid(), forward.RowVersion, 100), UserId));

        for (var i = 1; i <= FormerDailyNewLimit; i++)
        {
            var next = (await f.Study.GetNextAsync(collection.Id, UserId, PracticeDirection.SourceToTarget))!.Card!;
            Assert.True(await f.Study.RateAsync(new(collection.Id, next.CardId, AnkiRatings.Easy, Guid.NewGuid(), next.RowVersion, 100), UserId));
        }

        // Every new card in this direction has been studied...
        Assert.Null((await f.Study.GetNextAsync(collection.Id, UserId, PracticeDirection.SourceToTarget))!.Card);
        // ...but the reverse track and the word just reviewed are still available.
        var noteId = (await f.Context.AnkiCards.AsNoTracking().SingleAsync(c => c.Id == forward.CardId)).AnkiNoteId;
        var siblingId = (await f.Context.AnkiCards.AsNoTracking().SingleAsync(c => c.AnkiNoteId == noteId && c.Id != forward.CardId)).Id;
        Assert.NotNull((await f.Study.GetNextAsync(collection.Id, UserId, PracticeDirection.TargetToSource))!.Card);
        var reverse = (await f.Study.GetNextAsync(collection.Id, UserId, PracticeDirection.TargetToSource, siblingId))!.Card!;
        Assert.Equal(PracticeDirection.TargetToSource, reverse.Direction);
        Assert.Equal(forward.Answer, reverse.Prompt);
        Assert.Equal("Polish", reverse.PromptLanguage);
        Assert.All(await f.Context.AnkiCards.AsNoTracking().ToListAsync(), card => Assert.Null(card.BuriedUntil));

        var stats = await new AnkiStatisticsService(f.Context, new FakeTimeProvider(Now))
            .ReadSnapshotAsync(collection.Id, UserId, PracticeDirection.SourceToTarget);
        Assert.Equal(FormerDailyNewLimit + 1, stats!.Counts.StudiedToday);
        Assert.Equal(FormerDailyNewLimit + 1, stats.Counts.Total);
        var reverseStats = await new AnkiStatisticsService(f.Context, new FakeTimeProvider(Now))
            .ReadSnapshotAsync(collection.Id, UserId, PracticeDirection.TargetToSource);
        Assert.Equal(0, reverseStats!.Counts.StudiedToday);
    }

    [Fact]
    public async Task Membership_save_adds_both_directions_and_removal_survives_quiz_sync()
    {
        await using var f = await Fixture.CreateAsync();
        var collection = await f.Collections.GetOrCreateAsync(UserId, "Polish", "UTC");
        await f.Collections.AddQuizAsync(new(collection.Id, f.Quiz.Id, true, true, false, false), UserId);
        var sentence = await f.Context.QuizSentences.OrderBy(s => s.Text).FirstAsync();

        Assert.True(await f.Collections.SetMembershipAsync(collection.Id,
            [new(f.Quiz.Id, PracticeItemType.Sentences, sentence.Id.ToString())],
            [new(f.Quiz.Id, PracticeItemType.Words, f.Word.Id)], UserId));
        await f.Collections.SyncQuizAsync(f.Quiz.Id);

        var cards = await f.Context.AnkiCards.AsNoTracking().Include(c => c.Note).ToListAsync();
        Assert.All(cards.Where(c => c.Note.WordId == f.Word.Id), card =>
        {
            Assert.False(card.IsActive);
            Assert.True(card.ExcludedFromQuizLink);
        });
        Assert.Equal(2, cards.Count(c => c.Note.WordId == f.SecondWord.Id && c.IsActive));
        var sentenceCards = cards.Where(c => c.Note.SentenceId == sentence.Id).ToList();
        Assert.Equal(2, sentenceCards.Count);
        Assert.All(sentenceCards, card => Assert.True(card.IsActive && card.DirectlyIncluded));

        // Ticking the word again restores the same cards and their history.
        Assert.True(await f.Collections.SetMembershipAsync(collection.Id,
            [new(f.Quiz.Id, PracticeItemType.Words, f.Word.Id)], [], UserId));
        Assert.Equal(2, await f.Context.AnkiCards.CountAsync(c => c.Note.WordId == f.Word.Id && c.IsActive));
        Assert.Equal(2, await f.Context.AnkiCards.CountAsync(c => c.Note.WordId == f.Word.Id));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Removing_a_pair_excludes_linked_duplicates_and_preserves_history(bool sentences, bool existingCopies)
    {
        await using var f = await Fixture.CreateAsync();
        var collection = await f.Collections.GetOrCreateAsync(UserId, "Polish", "UTC");
        var type = sentences ? PracticeItemType.Sentences : PracticeItemType.Words;
        var sentenceItems = await f.Context.QuizSentences.OrderBy(item => item.Text).ToListAsync();
        var target = sentences ? sentenceItems[0].Text : f.Word.Lemma;
        var source = sentences ? sentenceItems[0].Translation : f.Word.Translation;
        if (existingCopies)
            await f.Collections.AddQuizAsync(new(collection.Id, f.Quiz.Id, !sentences, !sentences, sentences, sentences), UserId);
        if (sentences)
        {
            sentenceItems[1].Text = target;
            sentenceItems[1].Translation = source;
        }
        else
        {
            f.SecondWord.Lemma = target;
            f.SecondWord.Translation = source;
        }
        await f.Context.SaveChangesAsync();
        if (!existingCopies)
            await f.Collections.AddQuizAsync(new(collection.Id, f.Quiz.Id, !sentences, !sentences, sentences, sentences), UserId);
        // Either identical source may be encountered first during synchronization.
        var retainedNote = await f.Context.AnkiNotes.FirstAsync(note => note.ItemType == type && note.IsActive);
        var firstId = sentences ? retainedNote.SentenceId!.Value.ToString() : retainedNote.WordId!;
        var secondId = (sentences ? sentenceItems.Select(item => item.Id.ToString()) : new[] { f.Word.Id, f.SecondWord.Id })
            .Single(id => id != firstId);
        var reviewed = await f.Context.AnkiCards.FirstAsync(card => card.AnkiNoteId == retainedNote.Id);
        reviewed.ReviewCount = 5;
        reviewed.Stability = 12;
        var reviewedId = reviewed.Id;
        await f.Context.SaveChangesAsync();
        await f.Collections.SyncCollectionAsync(collection.Id);
        Assert.Equal(2, await f.Context.AnkiCards.CountAsync(card => card.IsActive));

        // A second linked quiz's skipped copy is represented by the same picker row.
        var otherQuiz = new Quiz { Id = Guid.NewGuid(), UserId = UserId, Name = "Other copies",
            SourceLanguage = "English", TargetLanguage = "Polish", Language = "Polish", ProcessingStatus = "Ready" };
        f.Context.Quizzes.Add(otherQuiz);
        if (sentences)
            f.Context.QuizSentences.Add(new QuizSentence { Id = Guid.NewGuid(), QuizId = otherQuiz.Id, Text = target, Translation = source });
        else
            f.Context.Words.Add(new Word { Id = "other-copy", QuizId = otherQuiz.Id, Lemma = target, Translation = source });
        await f.Context.SaveChangesAsync();
        await f.Collections.AddQuizAsync(new(collection.Id, otherQuiz.Id, !sentences, !sentences, sentences, sentences), UserId);
        Assert.True(await f.Collections.SetMembershipAsync(collection.Id, [], [new(f.Quiz.Id, type, firstId)], UserId));
        Assert.Equal(0, await f.Context.AnkiCards.CountAsync(card => card.IsActive));
        f.Context.ChangeTracker.Clear();
        var fresh = new AnkiCollectionService(f.Context, TimeProvider.System);
        for (var i = 0; i < 3; i++)
        {
            await fresh.SyncCollectionAsync(collection.Id);
            Assert.Equal(0, await f.Context.AnkiCards.CountAsync(card => card.IsActive));
        }
        Assert.Equal(5, (await f.Context.AnkiCards.SingleAsync(card => card.Id == reviewedId)).ReviewCount);
        Assert.Equal(12, (await f.Context.AnkiCards.SingleAsync(card => card.Id == reviewedId)).Stability);

        // Either source key may be selected again after the row has been unticked.
        Assert.True(await fresh.SetMembershipAsync(collection.Id, [new(f.Quiz.Id, type, secondId)], [], UserId));
        await fresh.SyncCollectionAsync(collection.Id);
        Assert.Equal(2, await f.Context.AnkiCards.CountAsync(card => card.IsActive));
        Assert.All(await f.Context.AnkiCards.Where(card => card.IsActive).Include(card => card.Note).ToListAsync(),
            card => Assert.Equal(secondId, sentences ? card.Note.SentenceId.ToString() : card.Note.WordId));
    }

    [Fact]
    public async Task Membership_save_rejects_foreign_collections_and_unknown_items_atomically()
    {
        await using var f = await Fixture.CreateAsync();
        var collection = await f.Collections.GetOrCreateAsync(UserId, "Polish", "UTC");

        Assert.False(await f.Collections.SetMembershipAsync(collection.Id,
            [new(f.Quiz.Id, PracticeItemType.Words, f.Word.Id)], [], "someone-else"));
        await Assert.ThrowsAsync<AnkiValidationException>(() => f.Collections.SetMembershipAsync(collection.Id,
            [new(f.Quiz.Id, PracticeItemType.Words, f.Word.Id), new(f.Quiz.Id, PracticeItemType.Words, "missing")], [], UserId));

        Assert.Empty(await f.Context.AnkiNotes.ToListAsync());
    }

    [Fact]
    public async Task Workspace_lists_every_source_language_and_marks_items_already_in_anki()
    {
        // The item list orders DateTimeOffset values, which SQLite cannot translate.
        await using var f = await Fixture.CreateAsync(useInMemoryDatabase: true);
        f.Context.Quizzes.AddRange(
            new Quiz { Id = Guid.NewGuid(), UserId = UserId, Name = "Från svenska", SourceLanguage = "Swedish", TargetLanguage = "Polish", Language = "Polish", ProcessingStatus = "Ready", CreatedAt = Now },
            new Quiz { Id = Guid.NewGuid(), UserId = UserId, Name = "Spanish", SourceLanguage = "English", TargetLanguage = "Spanish", Language = "Spanish", ProcessingStatus = "Ready", CreatedAt = Now },
            new Quiz { Id = Guid.NewGuid(), UserId = "someone-else", Name = "Foreign", SourceLanguage = "English", TargetLanguage = "Polish", Language = "Polish", ProcessingStatus = "Ready", CreatedAt = Now });
        await f.Context.SaveChangesAsync();

        var empty = await f.Collections.GetWorkspaceAsync(UserId, "Polish");
        Assert.Null(empty.Collection);
        Assert.Equal(new[] { "Basics", "Från svenska" }, empty.CompatibleQuizzes.Select(q => q.Name));
        Assert.All(empty.AvailableItems, item => Assert.False(item.InAnki));

        var collection = await f.Collections.GetOrCreateAsync(UserId, "Polish", "UTC");
        await f.Collections.AddItemAsync(new(collection.Id, f.Quiz.Id, "words", f.Word.Id, true, true), UserId);
        var workspace = await f.Collections.GetWorkspaceAsync(UserId, "Polish");

        Assert.Equal(collection.Id, workspace.Collection!.Id);
        Assert.Equal(4, workspace.AvailableItems.Count);
        Assert.Equal(new[] { PracticeItemType.Words, PracticeItemType.Words, PracticeItemType.Sentences, PracticeItemType.Sentences },
            workspace.AvailableItems.Select(item => item.ItemType));
        Assert.True(workspace.AvailableItems.Single(item => item.ItemId == f.Word.Id).InAnki);
        Assert.Single(workspace.AvailableItems, item => item.InAnki);
        Assert.All(workspace.AvailableItems, item => Assert.Equal("English", item.SourceLanguage));
    }

    [Fact]
    public async Task Whole_quiz_add_is_idempotent_and_syncs_add_edit_and_removal()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.Collections;
        var collection = await service.GetOrCreateAsync(UserId, "Polish", "UTC");
        var input = new AddAnkiQuizInput(collection.Id, fixture.Quiz.Id, true, true, true, false);

        Assert.True(await service.AddQuizAsync(input, UserId));
        Assert.True(await service.AddQuizAsync(input, UserId));
        Assert.Equal(4, await fixture.Context.AnkiNotes.CountAsync());
        Assert.Equal(6, await fixture.Context.AnkiCards.CountAsync());
        Assert.Single(await fixture.Context.AnkiQuizLinks.ToListAsync());

        fixture.Word.Lemma = "domy";
        fixture.Word.Translation = "houses";
        await fixture.Context.SaveChangesAsync();
        await service.SyncQuizAsync(fixture.Quiz.Id);
        var note = await fixture.Context.AnkiNotes.SingleAsync(item => item.WordId == fixture.Word.Id);
        Assert.Equal("domy", note.TargetText);
        Assert.Equal("houses", note.SourceText);

        fixture.Context.Words.Remove(fixture.Word);
        await fixture.Context.SaveChangesAsync();
        await service.SyncQuizAsync(fixture.Quiz.Id);
        Assert.False((await fixture.Context.AnkiNotes.SingleAsync(item => item.Id == note.Id)).IsActive);
        Assert.All(await fixture.Context.AnkiCards.Where(card => card.AnkiNoteId == note.Id).ToListAsync(), card => Assert.False(card.IsActive));
    }

    [Fact]
    public async Task Individual_sentence_from_picker_creates_both_cards_idempotently()
    {
        // The picker orders DateTimeOffset values, which SQLite cannot translate.
        await using var fixture = await Fixture.CreateAsync(useInMemoryDatabase: true);
        var collection = await fixture.Collections.GetOrCreateAsync(UserId, "Polish", "UTC");
        var workspace = await fixture.Collections.GetWorkspaceAsync(UserId, "Polish");
        var item = workspace.AvailableItems.First(item => item.ItemType == PracticeItemType.Sentences);
        var input = new AddAnkiItemInput(collection.Id, item.QuizId, item.ItemType, item.ItemId, true, true);

        Assert.True(await fixture.Collections.AddItemAsync(input, UserId));
        Assert.True(await fixture.Collections.AddItemAsync(input, UserId));

        var note = Assert.Single(await fixture.Context.AnkiNotes.ToListAsync());
        Assert.Equal(Guid.Parse(item.ItemId), note.SentenceId);
        Assert.Null(note.WordId);
        Assert.Equal(PracticeItemType.Sentences, note.ItemType);
        Assert.Equal(item.TargetText, note.TargetText);
        Assert.Equal(item.SourceText, note.SourceText);
        var cards = await fixture.Context.AnkiCards.ToListAsync();
        Assert.Equal(2, cards.Count);
        Assert.Contains(cards, card => card.Direction == PracticeDirection.SourceToTarget);
        Assert.Contains(cards, card => card.Direction == PracticeDirection.TargetToSource);
        Assert.All(cards, card =>
        {
            Assert.Equal(note.Id, card.AnkiNoteId);
            Assert.True(card.IsActive);
            Assert.True(card.DirectlyIncluded);
        });
    }

    [Theory]
    [InlineData("sentence")]
    [InlineData("SENTENCE")]
    [InlineData("sentences")]
    public async Task Individual_sentence_accepts_singular_and_canonical_types(string itemType)
    {
        await using var fixture = await Fixture.CreateAsync();
        var collection = await fixture.Collections.GetOrCreateAsync(UserId, "Polish", "UTC");
        var sentence = await fixture.Context.QuizSentences.FirstAsync();

        Assert.True(await fixture.Collections.AddItemAsync(
            new(collection.Id, fixture.Quiz.Id, itemType, sentence.Id.ToString(), true, false), UserId));

        var card = Assert.Single(await fixture.Context.AnkiCards.Include(card => card.Note).ToListAsync());
        Assert.Equal(sentence.Id, card.Note.SentenceId);
        Assert.Equal(PracticeItemType.Sentences, card.Note.ItemType);
        Assert.Equal(PracticeDirection.SourceToTarget, card.Direction);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("language")]
    [InlineData("quiz")]
    [InlineData("invalid-id")]
    public async Task Individual_sentence_retains_ownership_language_and_item_validation(string mismatch)
    {
        await using var fixture = await Fixture.CreateAsync();
        // Source languages may mix; only the learning language must match.
        var collection = await fixture.Collections.GetOrCreateAsync(UserId, mismatch == "language" ? "Spanish" : "Polish", "UTC");
        var sentence = await fixture.Context.QuizSentences.FirstAsync();
        var quizId = fixture.Quiz.Id;
        if (mismatch == "quiz")
        {
            var otherQuiz = new Quiz { Id = Guid.NewGuid(), UserId = UserId, Name = "Other", SourceLanguage = "English", TargetLanguage = "Polish", Language = "Polish", ProcessingStatus = "Ready", CreatedAt = Now };
            fixture.Context.Quizzes.Add(otherQuiz);
            await fixture.Context.SaveChangesAsync();
            quizId = otherQuiz.Id;
        }

        Assert.False(await fixture.Collections.AddItemAsync(
            new(collection.Id, quizId, "sentence", mismatch == "invalid-id" ? "bad-id" : sentence.Id.ToString(), true, false),
            mismatch == "owner" ? "another-user" : UserId));

        Assert.Empty(await fixture.Context.AnkiNotes.ToListAsync());
        Assert.Empty(await fixture.Context.AnkiCards.ToListAsync());
    }

    [Fact]
    public async Task Ownership_learning_language_and_direction_independence_are_enforced()
    {
        await using var fixture = await Fixture.CreateAsync();
        var wrongPair = await fixture.Collections.GetOrCreateAsync(UserId, "Spanish", "UTC");
        Assert.False(await fixture.Collections.AddQuizAsync(
            new(wrongPair.Id, fixture.Quiz.Id, true, false, false, false), UserId));
        Assert.False(await fixture.Collections.AddItemAsync(
            new(wrongPair.Id, fixture.Quiz.Id, "word", fixture.Word.Id, true, false), UserId));

        var collection = await fixture.Collections.GetOrCreateAsync(UserId, "Polish", "UTC");
        Assert.False(await fixture.Collections.AddItemAsync(
            new(collection.Id, fixture.Quiz.Id, "word", fixture.Word.Id, true, false), "another-user"));
        Assert.True(await fixture.Collections.AddItemAsync(
            new(collection.Id, fixture.Quiz.Id, "word", fixture.Word.Id, true, true), UserId));
        var cards = await fixture.Context.AnkiCards.Include(card => card.Note).Where(card => card.Note.AnkiCollectionId == collection.Id).ToListAsync();
        Assert.Equal(2, cards.Count);

        var forward = cards.Single(card => card.Direction == PracticeDirection.SourceToTarget);
        Assert.True(await fixture.Collections.RemoveCardAsync(forward.Id, UserId));
        Assert.False((await fixture.Context.AnkiCards.FindAsync(forward.Id))!.IsActive);
        Assert.True((await fixture.Context.AnkiCards.FindAsync(cards.Single(card => card.Id != forward.Id).Id))!.IsActive);
    }

    [Fact]
    public async Task Language_scoped_queries_only_return_matching_owned_collections_and_cards()
    {
        await using var fixture = await Fixture.CreateAsync();
        var polish = await fixture.Collections.GetOrCreateAsync(UserId, "pl", "UTC");
        Assert.Equal("Polish", polish.TargetLanguage);
        await fixture.Collections.GetOrCreateAsync(UserId, "Spanish", "UTC");

        Assert.Equal(polish.Id, (await fixture.Collections.FindForLanguageAsync(UserId, "polish"))!.Id);
        Assert.True(await fixture.Collections.IsOwnedByLanguageAsync(polish.Id, "POLISH", UserId));
        Assert.False(await fixture.Collections.IsOwnedByLanguageAsync(polish.Id, "Spanish", UserId));
        Assert.False(await fixture.Collections.IsOwnedByLanguageAsync(polish.Id, "Polish", "someone-else"));
    }

    [Fact]
    public async Task Language_scoped_queries_use_canonical_names_under_Turkish_request_culture()
    {
        await using var fixture = await Fixture.CreateAsync();
        var quiz = new Quiz
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            Name = "Indonesian",
            SourceLanguage = "English",
            TargetLanguage = "Indonesian",
            Language = "Indonesian",
            ProcessingStatus = "Ready",
            CreatedAt = Now,
        };
        var word = new Word
        {
            Id = "word-indonesian",
            QuizId = quiz.Id,
            Lemma = "indeks",
            Translation = "index",
            CreatedAt = Now,
        };
        fixture.Context.AddRange(quiz, word);
        await fixture.Context.SaveChangesAsync();
        var collection = await fixture.Collections.GetOrCreateAsync(UserId, "Indonesian", "UTC");
        Assert.True(await fixture.Collections.AddItemAsync(
            new(collection.Id, quiz.Id, "word", word.Id, true, false),
            UserId));
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");

            Assert.Equal(collection.Id, (await fixture.Collections.FindForLanguageAsync(UserId, "Indonesian"))!.Id);
            Assert.True(await fixture.Collections.IsOwnedByLanguageAsync(
                collection.Id,
                "Indonesian",
                UserId));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task Rating_is_idempotent_and_new_cards_remain_available_after_twenty_reviews()
    {
        await using var fixture = await Fixture.CreateAsync();
        var collection = await fixture.Collections.GetOrCreateAsync(UserId, "Polish", "UTC");
        await AddMoreThanADaysNewCardsAsync(fixture, collection.Id);

        var first = (await fixture.Study.GetNextAsync(collection.Id, UserId, PracticeDirection.SourceToTarget))!.Card!;
        Assert.Equal(fixture.Quiz.Id, first.QuizId);
        var token = Guid.NewGuid();
        var rating = new RateAnkiCardInput(collection.Id, first.CardId, AnkiRatings.Easy, token, first.RowVersion, 250);
        Assert.True(await fixture.Study.RateAsync(rating, UserId));
        Assert.False(await fixture.Study.RateAsync(rating, UserId));
        Assert.Single(await fixture.Context.AnkiReviews.ToListAsync());

        for (var i = 1; i < FormerDailyNewLimit; i++)
        {
            var next = (await fixture.Study.GetNextAsync(collection.Id, UserId, PracticeDirection.SourceToTarget))!.Card!;
            Assert.True(await fixture.Study.RateAsync(new(collection.Id, next.CardId, AnkiRatings.Easy, Guid.NewGuid(), next.RowVersion, 100), UserId));
        }
        Assert.Equal(FormerDailyNewLimit, await fixture.Context.AnkiReviews.CountAsync());
        var after = await fixture.Study.GetNextAsync(collection.Id, UserId, PracticeDirection.SourceToTarget);
        Assert.NotNull(after);
        Assert.NotNull(after!.Card);
        Assert.Equal(1, after.Card.NewRemaining);
        Assert.Equal("UTC", after.TimeZoneId);
        Assert.Equal(PracticeDirection.SourceToTarget, after.Direction);
        Assert.Equal(2, await fixture.Context.AnkiCards.CountAsync(card => card.Note.WordId == fixture.Word.Id));
    }

    [Fact]
    public async Task Reveal_can_pin_the_exact_card_selected_by_the_previous_request()
    {
        await using var fixture = await Fixture.CreateAsync();
        var collection = await fixture.Collections.GetOrCreateAsync(UserId, "Polish", "UTC");
        await fixture.Collections.AddItemAsync(new(collection.Id, fixture.Quiz.Id, "word", fixture.Word.Id, true, false), UserId);
        await fixture.Collections.AddItemAsync(new(collection.Id, fixture.Quiz.Id, "word", fixture.SecondWord.Id, true, false), UserId);

        var secondCardId = await fixture.Context.AnkiCards
            .Where(card => card.Note.WordId == fixture.SecondWord.Id)
            .Select(card => card.Id)
            .SingleAsync();
        var pinned = await fixture.Study.GetNextAsync(collection.Id, UserId, PracticeDirection.SourceToTarget, secondCardId);

        Assert.Equal(secondCardId, pinned!.Card!.CardId);
        Assert.Equal("cat", pinned.Card.Prompt);
    }

    [SqlServerFact]
    public async Task SqlServer_rating_persists_review_with_generated_rowversion()
    {
        await SqlServerTestDatabase.RunAsync("anki-rating", async context =>
        {
            context.Users.Add(new ApplicationUser
            {
                Id = UserId,
                UserName = "anki@example.test",
                NormalizedUserName = "ANKI@EXAMPLE.TEST",
            });
            var quiz = new Quiz
            {
                Id = Guid.NewGuid(),
                UserId = UserId,
                Name = "SQL rating",
                SourceLanguage = "English",
                TargetLanguage = "Polish",
                Language = "Polish",
                ProcessingStatus = "Ready",
                CreatedAt = Now,
            };
            var word = new Word
            {
                Id = $"word-{Guid.NewGuid():N}",
                QuizId = quiz.Id,
                Lemma = "dom",
                Translation = "house",
                CreatedAt = Now,
            };
            context.AddRange(quiz, word);
            await context.SaveChangesAsync();

            var clock = new FakeTimeProvider(Now);
            var collections = new AnkiCollectionService(context, clock);
            var study = new AnkiStudyService(context, collections, new Fsrs6AnkiScheduler(), clock);
            var collection = await collections.GetOrCreateAsync(UserId, "Polish", "UTC");
            Assert.True(await collections.AddItemAsync(
                new(collection.Id, quiz.Id, "word", word.Id, true, false), UserId));

            var card = (await study.GetNextAsync(collection.Id, UserId, PracticeDirection.SourceToTarget))!.Card!;
            Assert.NotEmpty(card.RowVersion);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE AnkiCards SET ReviewCount = ReviewCount + 1 WHERE Id = {card.CardId}");
            // MVC resolves each request with a fresh scoped DbContext. Clear the setup/read
            // graph so the rating path exercises SQL Server exactly as the POST request does.
            context.ChangeTracker.Clear();
            await Assert.ThrowsAsync<AnkiReviewConflictException>(() => study.RateAsync(new(
                collection.Id,
                card.CardId,
                AnkiRatings.Good,
                Guid.NewGuid(),
                card.RowVersion,
                250), UserId));
            var currentRowVersion = Convert.ToBase64String(await context.AnkiCards.AsNoTracking()
                .Where(candidate => candidate.Id == card.CardId)
                .Select(candidate => candidate.RowVersion)
                .SingleAsync());
            Assert.True(await study.RateAsync(new(
                collection.Id,
                card.CardId,
                AnkiRatings.Good,
                Guid.NewGuid(),
                currentRowVersion,
                250), UserId));
            Assert.Single(await context.AnkiReviews.ToListAsync());
            var summary = Assert.Single(await collections.ListAsync(UserId));
            Assert.Equal(1, summary.Counts.StudiedToday);
        });
    }

    [Fact]
    public void Collection_day_uses_timezone_local_midnight()
    {
        var start = AnkiCollectionService.StartOfCollectionDay("Europe/Stockholm", Now);
        Assert.Equal(new DateTimeOffset(2026, 8, 14, 22, 0, 0, TimeSpan.Zero), start);
    }

    // Inserts the same (user, language) collection from another context just before
    // the service's own insert, as a concurrent request would.
    [Fact]
    public async Task Study_suggestions_count_only_the_practice_direction()
    {
        await using var f = await Fixture.CreateAsync();
        var collection = await f.Collections.GetOrCreateAsync(UserId, "Polish", "UTC");
        await AddMoreThanADaysNewCardsAsync(f, collection.Id);
        for (var i = 0; i < FormerDailyNewLimit; i++)
        {
            var next = (await f.Study.GetNextAsync(collection.Id, UserId, PracticeDirection.SourceToTarget))!.Card!;
            Assert.True(await f.Study.RateAsync(new(collection.Id, next.CardId, AnkiRatings.Easy, Guid.NewGuid(), next.RowVersion, 100), UserId));
        }

        // The twenty-first new card is still available and suggested.
        Assert.NotNull((await f.Study.GetNextAsync(collection.Id, UserId, PracticeDirection.SourceToTarget))!.Card);
        var counts = (await f.Collections.ReadCountsAsync(collection.Id, UserId))!;
        Assert.Equal(1, counts.New);
        Assert.Equal(FormerDailyNewLimit, counts.StudiedToday);

        // The reverse track still contains every new card.
        Assert.True(await f.Collections.SetPracticeDirectionAsync(collection.Id, PracticeDirection.TargetToSource, UserId));
        counts = (await f.Collections.ReadCountsAsync(collection.Id, UserId))!;
        Assert.Equal(FormerDailyNewLimit + 1, counts.New);
        Assert.Equal(0, counts.StudiedToday);
    }

    [Fact]
    public async Task Adding_a_linked_quiz_again_keeps_its_sentences_and_unticked_items()
    {
        await using var f = await Fixture.CreateAsync();
        Assert.True(await f.Collections.AddQuizForLanguageAsync("Polish", "UTC", f.Quiz.Id, includeSentences: true, UserId));
        Assert.True(await f.Collections.SetMembershipForLanguageAsync("Polish", "UTC", [],
            [new AnkiItemKey(f.Quiz.Id, PracticeItemType.Words, f.Word.Id)], UserId));

        Assert.True(await f.Collections.AddQuizForLanguageAsync("Polish", "UTC", f.Quiz.Id, includeSentences: false, UserId));

        f.Context.ChangeTracker.Clear();
        var link = await f.Context.AnkiQuizLinks.SingleAsync();
        Assert.True(link.SentencesSourceToTarget && link.SentencesTargetToSource);
        Assert.Equal(4, await f.Context.AnkiCards.CountAsync(card => card.IsActive && card.Note.SentenceId != null));
        Assert.Equal(0, await f.Context.AnkiCards.CountAsync(card => card.IsActive && card.Note.WordId == f.Word.Id));
        Assert.Equal(2, await f.Context.AnkiCards.CountAsync(card => card.IsActive && card.Note.WordId == f.SecondWord.Id));
    }

    [Fact]
    public async Task Adding_a_linked_quiz_again_with_sentences_adds_them_and_keeps_unticked_words()
    {
        await using var f = await Fixture.CreateAsync();
        var collection = await f.Collections.GetOrCreateAsync(UserId, "Polish", "UTC");
        // The assistant can link one direction; the browser add then widens it to both.
        Assert.True(await f.Collections.AddQuizAsync(new(collection.Id, f.Quiz.Id, true, false, false, false), UserId));
        Assert.True(await f.Collections.SetMembershipForLanguageAsync("Polish", "UTC", [],
            [new AnkiItemKey(f.Quiz.Id, PracticeItemType.Words, f.Word.Id)], UserId));

        Assert.True(await f.Collections.AddQuizForLanguageAsync("Polish", "UTC", f.Quiz.Id, includeSentences: true, UserId));

        f.Context.ChangeTracker.Clear();
        var link = await f.Context.AnkiQuizLinks.SingleAsync();
        Assert.True(link.WordsSourceToTarget && link.WordsTargetToSource && link.SentencesSourceToTarget && link.SentencesTargetToSource);
        Assert.Equal(4, await f.Context.AnkiCards.CountAsync(card => card.IsActive && card.Note.SentenceId != null));
        Assert.Equal(2, await f.Context.AnkiCards.CountAsync(card => card.IsActive && card.Note.WordId == f.SecondWord.Id));
        Assert.Equal(0, await f.Context.AnkiCards.CountAsync(card => card.IsActive && card.Note.WordId == f.Word.Id));
    }

    [Fact]
    public async Task Widening_a_quiz_link_preserves_the_remaining_direction_of_a_partially_removed_word()
    {
        await using var f = await Fixture.CreateAsync();
        Assert.True(await f.Collections.AddQuizForLanguageAsync("Polish", "UTC", f.Quiz.Id, false, UserId));
        var forward = await f.Context.AnkiCards.SingleAsync(card => card.Note.WordId == f.Word.Id
            && card.Direction == PracticeDirection.SourceToTarget);
        var reverse = await f.Context.AnkiCards.SingleAsync(card => card.Note.WordId == f.Word.Id
            && card.Direction == PracticeDirection.TargetToSource);
        reverse.ReviewCount = 4;
        reverse.Stability = 12;
        reverse.DueAt = Now.AddDays(5);
        await f.Context.SaveChangesAsync();
        Assert.True(await f.Collections.RemoveCardAsync(forward.Id, UserId));

        Assert.True(await f.Collections.AddQuizForLanguageAsync("Polish", "UTC", f.Quiz.Id, true, UserId));

        f.Context.ChangeTracker.Clear();
        var storedForward = await f.Context.AnkiCards.SingleAsync(card => card.Id == forward.Id);
        var storedReverse = await f.Context.AnkiCards.SingleAsync(card => card.Id == reverse.Id);
        Assert.False(storedForward.IsActive);
        Assert.True(storedForward.ExcludedFromQuizLink);
        Assert.True(storedReverse.IsActive);
        Assert.False(storedReverse.ExcludedFromQuizLink);
        Assert.Equal(4, storedReverse.ReviewCount);
        Assert.Equal(12, storedReverse.Stability);
        Assert.Equal(Now.AddDays(5), storedReverse.DueAt);
        Assert.Equal(4, await f.Context.AnkiCards.CountAsync(card => card.IsActive && card.Note.SentenceId != null));
    }

    [Fact]
    public async Task Get_or_create_inside_a_caller_transaction_recovers_from_a_duplicate_collection()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var plain = new DbContextOptionsBuilder<GlosifyContext>().UseSqlite(connection).Options;
        await using (var setup = new GlosifyContext(plain))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Users.Add(new ApplicationUser { Id = UserId, UserName = "anki@example.test", NormalizedUserName = "ANKI@EXAMPLE.TEST" });
            await setup.SaveChangesAsync();
        }
        var competitorId = Guid.NewGuid();
        var race = new CompetingCollectionInterceptor(plain, competitorId, shareTransaction: true);
        await using var context = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>()
            .UseSqlite(connection).AddInterceptors(race).Options);
        // The assistant applies changes inside its run transaction. The duplicate insert
        // must not abort it: EF rolls back to its savepoint and the winner is reused.
        await using var transaction = await context.Database.BeginTransactionAsync();

        var collection = await new AnkiCollectionService(context, new FakeTimeProvider(Now))
            .GetOrCreateAsync(UserId, "Polish", "Europe/Stockholm");
        await transaction.CommitAsync();

        Assert.True(race.Fired);
        Assert.Equal(competitorId, collection.Id);
        await using var check = new GlosifyContext(plain);
        var stored = Assert.Single(await check.AnkiCollections.ToListAsync());
        Assert.Equal(competitorId, stored.Id);
        Assert.Equal("Europe/Stockholm", stored.TimeZoneId);
    }

    private sealed class CompetingCollectionInterceptor(DbContextOptions<GlosifyContext> options, Guid competitorId,
        bool shareTransaction = false) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && eventData.Context!.ChangeTracker.Entries<AnkiCollection>().Any(entry => entry.State == EntityState.Added))
            {
                Fired = true;
                await using var other = new GlosifyContext(options);
                // SQLite cannot interleave two writers, so a shared transaction stands in for
                // a row another connection committed just before this insert.
                if (shareTransaction)
                    await other.Database.UseTransactionAsync(eventData.Context.Database.CurrentTransaction!.GetDbTransaction(), cancellationToken);
                other.AnkiCollections.Add(new AnkiCollection { Id = competitorId, UserId = UserId, Name = "Polish", TargetLanguage = "Polish", TimeZoneId = "UTC" });
                await other.SaveChangesAsync(cancellationToken);
            }
            return result;
        }
    }

    private static async Task AddMoreThanADaysNewCardsAsync(Fixture fixture, Guid collectionId)
    {
        for (var i = 2; i <= FormerDailyNewLimit; i++)
            fixture.Context.Words.Add(new Word { Id = $"extra-{i}", QuizId = fixture.Quiz.Id,
                Lemma = $"word-{i}", Translation = $"translation-{i}", CreatedAt = Now.AddMinutes(i) });
        await fixture.Context.SaveChangesAsync();
        await fixture.Collections.AddQuizAsync(new(collectionId, fixture.Quiz.Id, true, true, false, false), UserId);
    }

    [Fact]
    public async Task Edits_that_create_duplicate_pairs_preserve_the_reviewed_card_across_syncs()
    {
        await using var f = await Fixture.CreateAsync();
        var collection = await f.Collections.GetOrCreateAsync(UserId, "Polish", "UTC");
        await f.Collections.AddQuizAsync(new(collection.Id, f.Quiz.Id, true, true, false, false), UserId);
        var reviewed = await f.Context.AnkiCards.FirstAsync(card => card.Note.WordId == f.Word.Id);
        reviewed.ReviewCount = 5;
        reviewed.Stability = 12;
        f.SecondWord.Lemma = f.Word.Lemma;
        f.SecondWord.Translation = f.Word.Translation;
        await f.Context.SaveChangesAsync();
        for (var i = 0; i < 3; i++)
        {
            await f.Collections.SyncCollectionAsync(collection.Id);
            Assert.Equal(2, await f.Context.AnkiCards.CountAsync(card => card.IsActive));
            Assert.True(reviewed.IsActive);
            Assert.Equal(5, reviewed.ReviewCount);
            Assert.Equal(12, reviewed.Stability);
        }
        await f.Collections.SetMembershipAsync(collection.Id,
            [new(f.Quiz.Id, PracticeItemType.Words, f.SecondWord.Id)], [], UserId);
        Assert.Equal(2, await f.Context.AnkiCards.CountAsync(card => card.IsActive));
    }

    [Fact]
    public async Task Identical_pairs_in_one_membership_batch_create_one_note()
    {
        await using var f = await Fixture.CreateAsync();
        f.SecondWord.Lemma = f.Word.Lemma;
        f.SecondWord.Translation = f.Word.Translation;
        await f.Context.SaveChangesAsync();
        var collection = await f.Collections.GetOrCreateAsync(UserId, "Polish", "UTC");
        await f.Collections.SetMembershipAsync(collection.Id,
            [new(f.Quiz.Id, PracticeItemType.Words, f.Word.Id), new(f.Quiz.Id, PracticeItemType.Words, f.SecondWord.Id)], [], UserId);
        Assert.Single(await f.Context.AnkiNotes.ToListAsync());
        Assert.Equal(2, await f.Context.AnkiCards.CountAsync(card => card.IsActive));
    }

    [Theory]
    [InlineData("single")]
    [InlineData("membership")]
    [InlineData("assistant")]
    [InlineData("quiz")]
    public async Task Exact_pairs_from_different_quizzes_are_added_only_once(string path)
    {
        await using var f = await Fixture.CreateAsync();
        var collection = await f.Collections.GetOrCreateAsync(UserId, "Polish", "UTC");
        await f.Collections.AddItemAsync(new(collection.Id, f.Quiz.Id, "word", f.Word.Id, true, true), UserId);
        var quiz = new Quiz { Id = Guid.NewGuid(), UserId = UserId, Name = "Duplicates",
            SourceLanguage = "English", TargetLanguage = "Polish", Language = "Polish", ProcessingStatus = "Ready", CreatedAt = Now };
        var duplicate = new Word { Id = "duplicate", QuizId = quiz.Id, Lemma = "dom", Translation = "house", CreatedAt = Now };
        f.Context.AddRange(quiz, duplicate);
        await f.Context.SaveChangesAsync();
        f.Context.ChangeTracker.Clear();
        switch (path)
        {
            case "single":
                Assert.True(await f.Collections.AddItemAsync(new(collection.Id, quiz.Id, "word", duplicate.Id, true, true), UserId));
                break;
            case "membership":
                Assert.True(await f.Collections.SetMembershipAsync(collection.Id,
                    [new(quiz.Id, PracticeItemType.Words, duplicate.Id)], [], UserId));
                break;
            case "assistant":
                var result = await f.Collections.AddItemsAsync(new(collection.Id, quiz.Id, PracticeItemType.Words, [duplicate.Id], true, true), UserId);
                Assert.Equal(0, result.CardsAdded);
                break;
            case "quiz":
                Assert.True(await f.Collections.AddQuizAsync(new(collection.Id, quiz.Id, true, true, false, false), UserId));
                break;
        }
        await f.Collections.SyncCollectionAsync(collection.Id);
        Assert.Equal(2, await f.Context.AnkiCards.CountAsync(card => card.IsActive));
        Assert.Equal(f.Word.Id, (await f.Context.AnkiNotes.SingleAsync(note => note.IsActive)).WordId);
    }

    [Theory]
    [InlineData("dom", "home")]
    [InlineData("Dom", "house")]
    [InlineData("dom ", "house")]
    [InlineData("dom", "House")]
    public async Task Different_pairs_are_kept(string word, string translation)
    {
        await using var f = await Fixture.CreateAsync();
        f.SecondWord.Lemma = word;
        f.SecondWord.Translation = translation;
        await f.Context.SaveChangesAsync();
        var collection = await f.Collections.GetOrCreateAsync(UserId, "Polish", "UTC");
        await f.Collections.SetMembershipAsync(collection.Id,
            [new(f.Quiz.Id, PracticeItemType.Words, f.Word.Id), new(f.Quiz.Id, PracticeItemType.Words, f.SecondWord.Id)], [], UserId);
        await f.Collections.SyncCollectionAsync(collection.Id);
        Assert.Equal(4, await f.Context.AnkiCards.CountAsync(card => card.IsActive));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public GlosifyContext Context { get; }
        public AnkiCollectionService Collections { get; }
        public AnkiStudyService Study { get; }
        public Quiz Quiz { get; }
        public Word Word { get; }
        public Word SecondWord { get; }

        private Fixture(SqliteConnection connection, GlosifyContext context, FakeTimeProvider clock,
            Quiz quiz, Word word, Word secondWord)
        {
            _connection = connection;
            Context = context;
            Quiz = quiz;
            Word = word;
            SecondWord = secondWord;
            Collections = new AnkiCollectionService(context, clock);
            Study = new AnkiStudyService(context, Collections, new Fsrs6AnkiScheduler(), clock);
        }

        public static async Task<Fixture> CreateAsync(bool useInMemoryDatabase = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var optionsBuilder = new DbContextOptionsBuilder<GlosifyContext>();
            if (useInMemoryDatabase)
                optionsBuilder.UseInMemoryDatabase(Guid.NewGuid().ToString("N"));
            else
                optionsBuilder.UseSqlite(connection);
            var options = optionsBuilder.Options;
            var context = new GlosifyContext(options);
            await context.Database.EnsureCreatedAsync();
            context.Users.Add(new ApplicationUser { Id = UserId, UserName = "anki@example.test", NormalizedUserName = "ANKI@EXAMPLE.TEST" });
            var quiz = new Quiz { Id = Guid.NewGuid(), UserId = UserId, Name = "Basics", SourceLanguage = "English", TargetLanguage = "Polish", Language = "Polish", ProcessingStatus = "Ready", CreatedAt = Now };
            var word = new Word { Id = "word-1", QuizId = quiz.Id, Lemma = "dom", Translation = "house", CreatedAt = Now };
            var secondWord = new Word { Id = "word-2", QuizId = quiz.Id, Lemma = "kot", Translation = "cat", CreatedAt = Now.AddMinutes(1) };
            context.AddRange(quiz, word, secondWord,
                new QuizSentence { Id = Guid.NewGuid(), QuizId = quiz.Id, Text = "To jest dom.", Translation = "This is a house.", CreatedAt = Now },
                new QuizSentence { Id = Guid.NewGuid(), QuizId = quiz.Id, Text = "To jest kot.", Translation = "This is a cat.", CreatedAt = Now.AddMinutes(1) });
            await context.SaveChangesAsync();
            return new Fixture(connection, context, new FakeTimeProvider(Now), quiz, word, secondWord);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
