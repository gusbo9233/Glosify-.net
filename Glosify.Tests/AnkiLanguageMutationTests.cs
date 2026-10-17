using System.Data;
using System.Data.Common;
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

    [SqlServerFact]
    public Task SqlServer_get_or_create_in_a_caller_transaction_reuses_a_concurrently_created_collection() =>
        SqlServerTestDatabase.RunAsync("anki-get-or-create-tx", async db =>
        {
            await SeedAsync(db);
            var plainOptions = new DbContextOptionsBuilder<GlosifyContext>().UseSqlServer(db.Database.GetConnectionString()).Options;
            var competitorId = Guid.NewGuid();
            var race = new CreateCompetingCollection(plainOptions, competitorId);
            await using var racingDb = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>()
                .UseSqlServer(db.Database.GetConnectionString()).AddInterceptors(race).Options);
            // As in an assistant apply: the caller owns the transaction around the first add.
            await using var transaction = await racingDb.Database.BeginTransactionAsync();

            var collection = await new AnkiCollectionService(racingDb, TimeProvider.System)
                .GetOrCreateAsync(UserId, "Polish", "Europe/Stockholm");
            await transaction.CommitAsync();

            Assert.True(race.Fired);
            Assert.Equal(competitorId, collection.Id);
            var stored = Assert.Single(await db.AnkiCollections.AsNoTracking().ToListAsync());
            Assert.Equal(competitorId, stored.Id);
            Assert.Equal("Europe/Stockholm", stored.TimeZoneId);
        });

    [Fact]
    public async Task Duplicate_collection_recovery_restores_accounting_and_keeps_caller_changes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<GlosifyContext>().UseSqlite(connection).Options;
        await using var seed = new GlosifyContext(options, Options.Create(new AbuseOptions()));
        await seed.Database.EnsureCreatedAsync();
        await VerifyAccountedRaceAsync(seed, new DbContextOptionsBuilder<GlosifyContext>()
            .UseSqlite(connection));
    }

    [SqlServerFact]
    public Task SqlServer_duplicate_collection_recovery_restores_accounting_and_keeps_caller_changes() =>
        SqlServerTestDatabase.RunAsync("anki-accounted-race", async database =>
        {
            var options = new DbContextOptionsBuilder<GlosifyContext>()
                .UseSqlServer(database.Database.GetConnectionString());
            await using var seed = new GlosifyContext(options.Options, Options.Create(new AbuseOptions()));
            await VerifyAccountedRaceAsync(seed, options);
        });

    [Fact]
    public async Task A_winning_collection_does_not_hide_an_unrelated_save_failure()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<GlosifyContext>().UseSqlite(connection).Options;
        await using var seed = new GlosifyContext(options, Options.Create(new AbuseOptions()));
        await seed.Database.EnsureCreatedAsync();
        seed.Add(new ResourceAccountingState { Id = 1, Ready = true });
        await seed.SaveChangesAsync();
        await SeedAsync(seed);
        await new AnkiCollectionService(seed, TimeProvider.System).GetOrCreateAsync(UserId, "Polish", "UTC");
        var failure = new FailQuizSave();
        var race = new MissFirstCollectionRead();
        await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>()
            .UseSqlite(connection).AddInterceptors(race, failure).Options, Options.Create(new AbuseOptions()));
        var quiz = await db.Quizzes.SingleAsync();
        quiz.Name = "Pending caller edit";

        var error = await Assert.ThrowsAsync<DbUpdateException>(() =>
            new AnkiCollectionService(db, TimeProvider.System).GetOrCreateAsync(UserId, "Polish", null));

        Assert.True(race.Fired);
        Assert.Same(failure.Error, error);
        // After the caller handles the error, retrying its edit must not save the
        // failed collection or any accounting mutations left by that failed save.
        failure.Armed = false;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal("Pending caller edit", (await db.Quizzes.SingleAsync()).Name);
        Assert.Single(await db.AnkiCollections.ToListAsync());
        Assert.Single(await db.Set<ResourceEntry>().Where(entry => entry.EntityType == nameof(AnkiCollection)).ToListAsync());
        Assert.Equal(1, (await db.Set<ResourceUsage>().SingleAsync(row => row.Scope == UserId && row.Resource == "collections")).Used);
    }

    private sealed class FailQuizSave : SaveChangesInterceptor
    {
        public bool Armed { get; set; } = true;
        public DbUpdateException? Error { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                var quiz = eventData.Context!.ChangeTracker.Entries<Quiz>().Single(entry => entry.State == EntityState.Modified);
                throw Error = new DbUpdateException("Simulated unrelated quiz write failure.", new InvalidOperationException(), [quiz]);
            }
            return ValueTask.FromResult(result);
        }
    }

    private static async Task VerifyAccountedRaceAsync(GlosifyContext seed, DbContextOptionsBuilder<GlosifyContext> options)
    {
        seed.Add(new ResourceAccountingState { Id = 1, Ready = true });
        await seed.SaveChangesAsync();
        var quizId = await SeedAsync(seed);
        var winner = await new AnkiCollectionService(seed, TimeProvider.System).GetOrCreateAsync(UserId, "Polish", "UTC");
        var race = new MissFirstCollectionRead();
        await using var db = new GlosifyContext(options.AddInterceptors(race).Options, Options.Create(new AbuseOptions()));
        // Exercise both existing tracked counters and snapshots loaded by the failed save.
        await db.Set<ResourceUsage>().LoadAsync();
        var quiz = await db.Quizzes.SingleAsync();
        quiz.Name = "Caller edit that must survive recovery";
        await using var transaction = await db.Database.BeginTransactionAsync();
        var service = new AnkiCollectionService(db, TimeProvider.System);

        var collection = await service.GetOrCreateAsync(UserId, "Polish", "Europe/Stockholm");
        Assert.True(await service.AddItemForLanguageAsync("Polish", "Europe/Stockholm",
            new(quizId, "word", "word-1"), UserId));
        // The caller can continue using its tracked entity after recovery.
        quiz.Name = "Caller edit after recovery";
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        Assert.True(race.Fired);
        Assert.Equal(winner.Id, collection.Id);
        db.ChangeTracker.Clear();
        Assert.Equal("Europe/Stockholm", (await db.AnkiCollections.SingleAsync()).TimeZoneId);
        Assert.Equal("Caller edit after recovery", (await db.Quizzes.SingleAsync()).Name);
        Assert.Equal(2, await db.AnkiCards.CountAsync(card => card.IsActive && card.Note.AnkiCollectionId == winner.Id));
        var entries = await db.Set<ResourceEntry>().ToListAsync();
        Assert.Single(entries, entry => entry.EntityType == nameof(AnkiCollection));
        Assert.Equal(1, (await db.Set<ResourceUsage>().SingleAsync(row => row.Scope == UserId && row.Resource == "collections")).Used);
        var expectedBytes = entries.Sum(entry => ResourceAccounting.Charges(entry.ChargesJson)["content_bytes"]);
        Assert.Equal(expectedBytes, (await db.Set<ResourceUsage>().SingleAsync(row => row.Scope == UserId && row.Resource == "content_bytes")).Used);
        Assert.Equal(expectedBytes, (await db.Set<ResourceUsage>().SingleAsync(row => row.Scope == ResourceAccounting.Site && row.Resource == "content_bytes")).Used);
    }

    // Simulate the initial lookup missing a winner that commits before SaveChanges.
    // The duplicate constraint failure and all accounting writes use the real provider.
    private sealed class MissFirstCollectionRead : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Fired || !command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
                || !command.CommandText.Contains("AnkiCollections", StringComparison.Ordinal)) return result;
            Fired = true;
            var empty = new DataTable();
            for (var i = 0; i < result.FieldCount; i++) empty.Columns.Add(result.GetName(i), result.GetFieldType(i));
            await result.DisposeAsync();
            return empty.CreateDataReader();
        }
    }

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
