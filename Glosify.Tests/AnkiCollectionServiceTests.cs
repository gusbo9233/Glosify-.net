using Glosify.Data;
using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Services.Anki;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Glosify.Tests;

public sealed class AnkiCollectionServiceTests
{
    private const string UserId = "anki-user";
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Whole_quiz_add_is_idempotent_and_syncs_add_edit_and_removal()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.Collections;
        var collection = await service.CreateAsync(new("Polish", "English", "Polish", "UTC"), UserId);
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
    public async Task Ownership_language_pair_and_direction_independence_are_enforced()
    {
        await using var fixture = await Fixture.CreateAsync();
        var wrongPair = await fixture.Collections.CreateAsync(new("Wrong", "Swedish", "Polish", "UTC"), UserId);
        Assert.False(await fixture.Collections.AddQuizAsync(
            new(wrongPair.Id, fixture.Quiz.Id, true, false, false, false), UserId));
        Assert.False(await fixture.Collections.AddItemAsync(
            new(wrongPair.Id, fixture.Quiz.Id, "word", fixture.Word.Id, true, false), UserId));

        var collection = await fixture.Collections.CreateAsync(new("Right", "English", "Polish", "UTC"), UserId);
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
    public async Task Collection_names_over_the_database_limit_are_rejected_without_truncation()
    {
        await using var fixture = await Fixture.CreateAsync();

        var exception = await Assert.ThrowsAsync<AnkiValidationException>(() =>
            fixture.Collections.CreateAsync(new(new string('a', 161), "English", "Polish", "UTC"), UserId));

        Assert.Contains("160", exception.Message, StringComparison.Ordinal);
        Assert.Empty(await fixture.Context.AnkiCollections.ToListAsync());
    }

    [Fact]
    public async Task Create_from_quiz_rejects_a_different_app_language()
    {
        await using var fixture = await Fixture.CreateAsync();

        var exception = await Assert.ThrowsAsync<AnkiValidationException>(() =>
            fixture.Collections.CreateFromQuizAsync(new(
                "Wrong language", fixture.Quiz.Id, "UTC", "Spanish",
                true, false, false, false), UserId));

        Assert.Contains("different app language", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Context.AnkiCollections.ToListAsync());
    }

    [Fact]
    public async Task Language_scoped_queries_only_return_matching_owned_collections_and_cards()
    {
        await using var fixture = await Fixture.CreateAsync();
        var polish = await fixture.Collections.CreateAsync(new("Polish", "English", "Polish", "UTC"), UserId);
        await fixture.Collections.CreateAsync(new("Spanish", "English", "Spanish", "UTC"), UserId);
        await fixture.Collections.AddItemAsync(
            new(polish.Id, fixture.Quiz.Id, "word", fixture.Word.Id, true, false), UserId);
        var cardId = await fixture.Context.AnkiCards.Select(card => card.Id).SingleAsync();

        var collections = await fixture.Collections.ListForLanguageAsync(UserId, "polish");

        Assert.Equal(polish.Id, Assert.Single(collections).Id);
        Assert.True(await fixture.Collections.IsOwnedByLanguageAsync(polish.Id, "POLISH", UserId));
        Assert.False(await fixture.Collections.IsOwnedByLanguageAsync(polish.Id, "Spanish", UserId));
        Assert.True(await fixture.Collections.IsCardInOwnedLanguageAsync(cardId, polish.Id, "Polish", UserId));
        Assert.False(await fixture.Collections.IsCardInOwnedLanguageAsync(cardId, polish.Id, "Spanish", UserId));
    }

    [Fact]
    public async Task Rating_is_idempotent_buries_sibling_and_daily_new_limit_is_durable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var collection = await fixture.Collections.CreateAsync(new("Daily", "English", "Polish", "UTC"), UserId);
        await fixture.Collections.UpdateSettingsAsync(collection.Id, .9, 1, 200, "UTC", UserId);
        await fixture.Collections.AddItemAsync(new(collection.Id, fixture.Quiz.Id, "word", fixture.Word.Id, true, true), UserId);

        var first = (await fixture.Study.GetNextAsync(collection.Id, UserId))!.Card!;
        var token = Guid.NewGuid();
        var rating = new RateAnkiCardInput(collection.Id, first.CardId, AnkiRatings.Easy, token, first.RowVersion, 250);
        Assert.True(await fixture.Study.RateAsync(rating, UserId));
        Assert.False(await fixture.Study.RateAsync(rating, UserId));
        Assert.Single(await fixture.Context.AnkiReviews.ToListAsync());
        var ratedNoteId = (await fixture.Context.AnkiCards.SingleAsync(card => card.Id == first.CardId)).AnkiNoteId;
        var siblings = await fixture.Context.AnkiCards
            .Where(card => card.AnkiNoteId == ratedNoteId && card.Id != first.CardId)
            .ToListAsync();
        Assert.NotEmpty(siblings);
        Assert.All(siblings, card => Assert.True(card.BuriedUntil.HasValue));

        await fixture.Collections.AddItemAsync(new(collection.Id, fixture.Quiz.Id, "word", fixture.SecondWord.Id, true, false), UserId);
        var after = await fixture.Study.GetNextAsync(collection.Id, UserId);
        Assert.NotNull(after);
        Assert.Null(after!.Card);
        Assert.Equal("UTC", after.TimeZoneId);
        Assert.Equal(2, await fixture.Context.AnkiCards.CountAsync(card => card.Note.WordId == fixture.Word.Id));
    }

    [Fact]
    public async Task Reveal_can_pin_the_exact_card_selected_by_the_previous_request()
    {
        await using var fixture = await Fixture.CreateAsync();
        var collection = await fixture.Collections.CreateAsync(new("Pinned", "English", "Polish", "UTC"), UserId);
        await fixture.Collections.AddItemAsync(new(collection.Id, fixture.Quiz.Id, "word", fixture.Word.Id, true, false), UserId);
        await fixture.Collections.AddItemAsync(new(collection.Id, fixture.Quiz.Id, "word", fixture.SecondWord.Id, true, false), UserId);

        var secondCardId = await fixture.Context.AnkiCards
            .Where(card => card.Note.WordId == fixture.SecondWord.Id)
            .Select(card => card.Id)
            .SingleAsync();
        var pinned = await fixture.Study.GetNextAsync(collection.Id, UserId, secondCardId);

        Assert.Equal(secondCardId, pinned!.Card!.CardId);
        Assert.Equal("cat", pinned.Card.Prompt);
    }

    [Fact]
    public async Task SqlServer_rating_persists_review_with_generated_rowversion()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
        if (string.IsNullOrWhiteSpace(configuredConnection))
            return;

        var connection = new SqlConnectionStringBuilder(configuredConnection)
        {
            InitialCatalog = $"glosify-anki-rating-{Guid.NewGuid():N}",
        };
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseSqlServer(connection.ConnectionString)
            .Options;
        await using var context = new GlosifyContext(options);
        try
        {
            await context.Database.EnsureCreatedAsync();
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
            var collection = await collections.CreateAsync(new("SQL", "English", "Polish", "UTC"), UserId);
            Assert.True(await collections.AddItemAsync(
                new(collection.Id, quiz.Id, "word", word.Id, true, false), UserId));

            var card = (await study.GetNextAsync(collection.Id, UserId))!.Card!;
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
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public void Collection_day_uses_timezone_local_midnight()
    {
        var start = AnkiCollectionService.StartOfCollectionDay("Europe/Stockholm", Now);
        Assert.Equal(new DateTimeOffset(2026, 8, 14, 22, 0, 0, TimeSpan.Zero), start);
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

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<GlosifyContext>().UseSqlite(connection).Options;
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
