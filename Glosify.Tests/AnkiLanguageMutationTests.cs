using Glosify.Data;
using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Services.Abuse;
using Glosify.Services.Anki;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class AnkiLanguageMutationTests
{
    private const string UserId = "anki-mutation-user";

    [Theory]
    [InlineData("quiz", false)]
    [InlineData("item", false)]
    [InlineData("items", false)]
    [InlineData("quiz", true)]
    [InlineData("item", true)]
    [InlineData("items", true)]
    public async Task Failed_card_write_rolls_back_collection_timezone_membership_and_accounting(string operation, bool existing)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var failure = new FailCardSave();
        await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>()
            .UseSqlite(connection).AddInterceptors(failure).Options, Options.Create(new AbuseOptions()));
        await db.Database.EnsureCreatedAsync();
        db.Add(new ResourceAccountingState { Id = 1, Ready = true });
        await db.SaveChangesAsync();
        var quizId = await SeedAsync(db);
        var service = new AnkiCollectionService(db, TimeProvider.System);
        if (existing) await service.GetOrCreateAsync(UserId, "Polish", "UTC");
        var entries = await db.Set<ResourceEntry>().AsNoTracking().Select(entry => entry.Id).ToListAsync();
        var usage = await db.Set<ResourceUsage>().AsNoTracking().ToDictionaryAsync(entry => (entry.Scope, entry.Resource), entry => entry.Used);
        failure.Armed = true;

        await Assert.ThrowsAsync<DbUpdateException>(() => operation switch
        {
            "quiz" => service.AddQuizForLanguageAsync("Polish", "Europe/Stockholm", quizId, false, UserId),
            "item" => service.AddItemForLanguageAsync("Polish", "Europe/Stockholm", new(quizId, "word", "word-1"), UserId),
            _ => service.SetMembershipForLanguageAsync("Polish", "Europe/Stockholm", [new(quizId, "words", "word-1")], [], UserId),
        });

        Assert.True(failure.Fired);
        var collections = await db.AnkiCollections.AsNoTracking().ToListAsync();
        if (existing) Assert.Equal("UTC", Assert.Single(collections).TimeZoneId);
        else Assert.Empty(collections);
        Assert.Empty(await db.AnkiNotes.ToListAsync());
        Assert.Empty(await db.AnkiCards.ToListAsync());
        Assert.Empty(await db.AnkiQuizLinks.ToListAsync());
        Assert.Equal(entries.Order(), (await db.Set<ResourceEntry>().Select(entry => entry.Id).ToListAsync()).Order());
        Assert.Equal(usage.OrderBy(pair => pair.Key), (await db.Set<ResourceUsage>()
            .ToDictionaryAsync(entry => (entry.Scope, entry.Resource), entry => entry.Used)).OrderBy(pair => pair.Key));
    }

    [SqlServerFact]
    public Task SqlServer_first_add_reuses_a_concurrently_created_collection() =>
        SqlServerTestDatabase.RunAsync("anki-first-add", async db =>
        {
            var quizId = await SeedAsync(db);
            var plainOptions = new DbContextOptionsBuilder<GlosifyContext>().UseSqlServer(db.Database.GetConnectionString()).Options;
            var competitorId = Guid.NewGuid();
            var race = new CreateCompetingCollection(plainOptions, competitorId);
            await using var racingDb = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>()
                .UseSqlServer(db.Database.GetConnectionString()).AddInterceptors(race).Options);
            var service = new AnkiCollectionService(racingDb, TimeProvider.System);

            Assert.True(await service.AddItemForLanguageAsync("Polish", "Europe/Stockholm", new(quizId, "word", "word-1"), UserId));

            Assert.True(race.Fired);
            var collection = Assert.Single(await db.AnkiCollections.AsNoTracking().ToListAsync());
            Assert.Equal(competitorId, collection.Id);
            Assert.Equal("Europe/Stockholm", collection.TimeZoneId);
            Assert.Equal(2, await db.AnkiCards.CountAsync(card => card.IsActive && card.Note.AnkiCollectionId == competitorId));
        });

    [Fact]
    public async Task Removing_stale_items_does_not_create_a_collection()
    {
        await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var quizId = await SeedAsync(db);
        var service = new AnkiCollectionService(db, TimeProvider.System);

        Assert.True(await service.SetMembershipForLanguageAsync("Polish", "UTC", [], [new(quizId, "words", "word-1")], UserId));

        Assert.Empty(await db.AnkiCollections.ToListAsync());
    }

    private static async Task<Guid> SeedAsync(GlosifyContext db)
    {
        var quizId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = UserId, UserName = "mutations@example.test" });
        db.Quizzes.Add(new Quiz { Id = quizId, UserId = UserId, Name = "Basics", SourceLanguage = "English", TargetLanguage = "Polish", Language = "Polish" });
        db.Words.Add(new Word { Id = "word-1", QuizId = quizId, Lemma = "dom", Translation = "house" });
        await db.SaveChangesAsync();
        return quizId;
    }

    private sealed class FailCardSave : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<AnkiCard>().Any(entry => entry.State == EntityState.Added))
            {
                Fired = true;
                throw new DbUpdateException("Simulated card write failure.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CreateCompetingCollection(DbContextOptions<GlosifyContext> options, Guid id) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && eventData.Context!.ChangeTracker.Entries<AnkiCollection>().Any(entry => entry.State == EntityState.Added))
            {
                Fired = true;
                await using var other = new GlosifyContext(options);
                other.AnkiCollections.Add(new AnkiCollection { Id = id, UserId = UserId, Name = "Polish", TargetLanguage = "Polish", TimeZoneId = "UTC" });
                await other.SaveChangesAsync(cancellationToken);
            }
            return result;
        }
    }
}
