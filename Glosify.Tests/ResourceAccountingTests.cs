using Microsoft.Extensions.DependencyInjection;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Abuse;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class ResourceAccountingTests
{
    [Fact]
    public void BackfillErrorsAreDistinctFromExhaustedSiteCapacity()
    {
        var initializing = Glosify.Infrastructure.Api.ApiExceptionMapper.Map(new ResourceQuotaException("accounting_initializing", true))!.Value;
        var full = Glosify.Infrastructure.Api.ApiExceptionMapper.Map(new ResourceQuotaException("content_bytes", true))!.Value;
        Assert.Equal(503, initializing.StatusCode);
        Assert.Equal("accounting_initializing", initializing.Code);
        Assert.Equal(503, full.StatusCode);
        Assert.Equal("site_capacity_exceeded", full.Code);
        Assert.NotEqual(initializing.Detail, full.Detail);
    }

    [Fact]
    public async Task QuizLimit_IsAcrossLanguages_AndDeletionFreesCapacity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>().UseSqlite(connection).Options,
            Options.Create(new AbuseOptions { Quizzes = 1 }));
        await Initialize(db);
        db.Quizzes.Add(Quiz("one", "Swedish")); await db.SaveChangesAsync();
        db.Quizzes.Add(Quiz("two", "German"));
        var error = await Assert.ThrowsAsync<ResourceQuotaException>(() => db.SaveChangesAsync());
        Assert.Equal("quizzes", error.Resource);
        Assert.Equal(1, await db.Quizzes.CountAsync());
        db.Remove(await db.Quizzes.SingleAsync()); await db.SaveChangesAsync();
        db.Quizzes.Add(Quiz("three", "German")); await db.SaveChangesAsync();
        Assert.Equal("three", (await db.Quizzes.SingleAsync()).Name);
        Assert.Equal(1, (await db.Set<ResourceUsage>().SingleAsync(x => x.Scope == "owner" && x.Resource == "quizzes")).Used);
    }

    [Fact]
    public async Task ItemsAndCopies_AreCheckedAtomically_AndCascadeReleasesCharges()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>().UseSqlite(connection).Options,
            Options.Create(new AbuseOptions { ItemsPerQuiz = 1 }));
        await Initialize(db);
        var quiz = Quiz("one", "Swedish"); db.Add(quiz); await db.SaveChangesAsync();
        db.Words.Add(new Word { Id = "a", QuizId = quiz.Id, Lemma = "hej", Translation = "hello" });
        db.Words.Add(new Word { Id = "b", QuizId = quiz.Id, Lemma = "ja", Translation = "yes" });
        await Assert.ThrowsAsync<ResourceQuotaException>(() => db.SaveChangesAsync());
        Assert.Equal(0, await db.Words.CountAsync());
        db.Words.Add(new Word { Id = "c", QuizId = quiz.Id, Lemma = "hej", Translation = "hello" }); await db.SaveChangesAsync();
        db.ChangeTracker.Clear(); db.Remove(await db.Quizzes.SingleAsync()); await db.SaveChangesAsync();
        Assert.Equal(0, await db.Words.CountAsync());
        Assert.Empty(await db.Set<ResourceEntry>().ToListAsync());
        Assert.All(await db.Set<ResourceUsage>().ToListAsync(), row => Assert.Equal(0, row.Used));
    }

    [Fact]
    public async Task Utf16ContentAndMetadata_AreCharged_AndGrowthStopsAtSiteLimit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>().UseSqlite(connection).Options,
            Options.Create(new AbuseOptions { SiteContentBytes = 1800 }));
        await Initialize(db);
        var quiz = Quiz("hej 👋", "Swedish"); db.Add(quiz); await db.SaveChangesAsync();
        var entry = await db.Set<ResourceEntry>().SingleAsync();
        var expected = 1024 + db.Entry(quiz).Properties.Where(p => p.Metadata.ClrType == typeof(string))
            .Sum(p => 2 * ((string?)p.CurrentValue)?.Length ?? 0);
        Assert.Equal(expected, ResourceAccounting.Charges(entry.ChargesJson)["content_bytes"]);
        quiz.Name = new string('x', 500);
        var error = await Assert.ThrowsAsync<ResourceQuotaException>(() => db.SaveChangesAsync());
        Assert.Equal("site_capacity_exceeded", error.Code);
        Assert.Equal("hej 👋", (await db.Quizzes.SingleAsync()).Name);
    }

    [SqlServerFact]
    public Task ConcurrentConnections_CannotBothTakeLastQuizSlot() => SqlServerTestDatabase.RunAsync("quota_race", async seed =>
    {
        seed.Add(new ResourceAccountingState { Id = 1, Ready = true });
        seed.Users.Add(new ApplicationUser { Id = "owner", UserName = "owner", Email = "owner@example.test" });
        await seed.SaveChangesAsync();
        var connection = seed.Database.GetConnectionString();
        async Task<bool> Create(string name)
        {
            await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>().UseSqlServer(connection).Options,
                Options.Create(new AbuseOptions { Quizzes = 1 }));
            db.Add(Quiz(name, "Swedish"));
            try { await db.SaveChangesAsync(); return true; }
            catch (ResourceQuotaException) { return false; }
        }
        var results = await Task.WhenAll(Create("one"), Create("two"));
        Assert.Single(results, x => x);
        Assert.Equal(1, await seed.Quizzes.CountAsync());
        Assert.Equal(1, (await seed.Set<ResourceUsage>().SingleAsync(x => x.Scope == "owner" && x.Resource == "quizzes")).Used);
    });

    [Fact]
    public async Task ReservationsPreventOversubscription_AndExpiredClaimsCannotCommit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<GlosifyContext>().UseSqlite(connection).Options;
        var limits = Options.Create(new AbuseOptions { Quizzes = 1 });
        await using var db = new GlosifyContext(dbOptions, limits); await Initialize(db);
        var quotas = new ResourceQuotaService(new Factory(dbOptions, limits), limits);
        var reservation = await quotas.ReserveAsync("owner", new() { ["quizzes"] = 1 });
        db.Add(Quiz("blocked", "Swedish"));
        await Assert.ThrowsAsync<ResourceQuotaException>(() => db.SaveChangesAsync());
        var usage = await quotas.GetUsageAsync("owner", default);
        Assert.Equal(1, usage.Single(x => x.Resource == "quizzes").Reserved);
        Assert.Equal(0, await db.Quizzes.CountAsync());
        var row = await db.Set<ResourceReservation>().SingleAsync(); row.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        db.Add(Quiz("expired", "Swedish")); db.ClaimedResourceReservation = (reservation, "owner");
        var error = await Assert.ThrowsAsync<ResourceQuotaException>(() => db.SaveChangesAsync());
        Assert.Equal("reservation_expired", error.Resource);
        Assert.Equal(0, await db.Quizzes.CountAsync());
        await quotas.ReleaseAsync(reservation, "owner");
        db.Add(Quiz("allowed", "Swedish")); await db.SaveChangesAsync();
        Assert.Equal("allowed", (await db.Quizzes.SingleAsync()).Name);
    }

    [SqlServerFact]
    public Task ActiveRequestsRenewCapacity_AndStopOnCompletion() => SqlServerTestDatabase.RunAsync("quota_renewal", async seed =>
    {
        seed.Add(new ResourceAccountingState { Id = 1, Ready = true });
        await seed.SaveChangesAsync();
        var options = new DbContextOptionsBuilder<GlosifyContext>().UseSqlServer(seed.Database.GetConnectionString()).Options;
        var limits = Options.Create(new AbuseOptions());
        var quotas = new ResourceQuotaService(new Factory(options, limits), limits);
        var id = await quotas.ReserveAsync("owner", new() { ["content_bytes"] = 100 }, lifetime: TimeSpan.FromMinutes(1));
        var before = (await seed.Set<ResourceReservation>().AsNoTracking().SingleAsync()).ExpiresAt;
        var request = new RequestResourceReservations();
        request.Track(id, "owner");
        using var active = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var renewal = request.RenewWhileActiveAsync(quotas, active, TimeSpan.FromMilliseconds(20));
        try
        {
            DateTimeOffset after;
            do
            {
                await Task.Delay(20, active.Token);
                after = (await seed.Set<ResourceReservation>().AsNoTracking().SingleAsync(active.Token)).ExpiresAt;
            } while (after <= before);
            Assert.True(after > before.AddMinutes(3));
        }
        finally { active.Cancel(); await renewal; }
        await request.ReleaseAsync(quotas);
        Assert.Empty(await seed.Set<ResourceReservation>().ToListAsync());
    });

    [Fact]
    public async Task RenewalCannotResurrectExpiredOrForeignReservations()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var settings = new DbContextOptionsBuilder<GlosifyContext>().UseSqlite(connection).Options;
        var limits = Options.Create(new AbuseOptions());
        await using var db = new GlosifyContext(settings, limits); await Initialize(db);
        var quotas = new ResourceQuotaService(new Factory(settings, limits), limits);
        var id = await quotas.ReserveAsync("owner", new() { ["content_bytes"] = 100 });
        var before = (await db.Set<ResourceReservation>().AsNoTracking().SingleAsync()).ExpiresAt;
        await Assert.ThrowsAsync<ResourceQuotaException>(() => quotas.RenewAsync(id, "another-user"));
        Assert.Equal(before, (await db.Set<ResourceReservation>().AsNoTracking().SingleAsync()).ExpiresAt);
        var row = await db.Set<ResourceReservation>().SingleAsync();
        row.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ResourceQuotaException>(() => quotas.RenewAsync(id, "owner"));
        Assert.Equal(row.ExpiresAt, (await db.Set<ResourceReservation>().AsNoTracking().SingleAsync()).ExpiresAt);
    }

    [SqlServerFact]
    public Task BackfillPreservesLegacyContentAboveLimit_AndAllowsShrinkingEdits() => SqlServerTestDatabase.RunAsync("quota_backfill", async seed =>
    {
        seed.Users.Add(new ApplicationUser { Id = "owner", UserName = "owner", Email = "owner@example.test" });
        seed.Quizzes.AddRange(Quiz("legacy one", "Swedish"), Quiz("legacy two", "German"));
        await seed.SaveChangesAsync();
        await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>().UseSqlServer(seed.Database.GetConnectionString()).Options,
            Options.Create(new AbuseOptions { Quizzes = 1, ContentBytes = 1 }));
        db.Add(Quiz("blocked until ready", "Swedish"));
        Assert.Equal("accounting_initializing", (await Assert.ThrowsAsync<ResourceQuotaException>(() => db.SaveChangesAsync())).Resource);
        using var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider();
        await ResourceMaintenanceService.RebuildAsync(db, services, default);
        Assert.Equal(2, await db.Quizzes.CountAsync());
        Assert.Equal(2, (await db.Set<ResourceUsage>().SingleAsync(x => x.Scope == "owner" && x.Resource == "quizzes")).Used);
        var quiz = await db.Quizzes.FirstAsync(); quiz.Name = "a"; await db.SaveChangesAsync();
        db.Add(Quiz("new", "Swedish"));
        await Assert.ThrowsAsync<ResourceQuotaException>(() => db.SaveChangesAsync());
        Assert.Equal(2, await db.Quizzes.CountAsync());
        db.Remove(await db.Quizzes.FirstAsync()); await db.SaveChangesAsync();
        Assert.Equal(1, await db.Quizzes.CountAsync());
    });

    [Fact]
    public async Task DeletedPdfBytesRemainChargedUntilCleanupCompletes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>().UseSqlite(connection).Options,
            Options.Create(new AbuseOptions { PdfBytes = 100 }));
        await Initialize(db);
        var book = new Glosify.Models.Library.BookDocument { Id = Guid.NewGuid(), UserId = "owner", Title = "book", BlobName = "owner/book.pdf", OriginalFileName = "book.pdf", FileSizeBytes = 100 };
        db.Add(book); await db.SaveChangesAsync(); db.Remove(book); await db.SaveChangesAsync();
        Assert.Equal(0, await db.BookDocuments.CountAsync());
        Assert.Equal(100, (await db.Set<ResourceUsage>().SingleAsync(x => x.Scope == "owner" && x.Resource == "pdf_bytes")).Used);
        var cleanup = await db.Set<BlobCleanupRequest>().SingleAsync(); Assert.Equal("owner/book.pdf", cleanup.BlobName);
        db.Remove(cleanup); await db.SaveChangesAsync();
        Assert.Equal(0, (await db.Set<ResourceUsage>().SingleAsync(x => x.Scope == "owner" && x.Resource == "pdf_bytes")).Used);
    }

    [SqlServerFact]
    public Task TransientCommitFailureReplaysContentAndCountersTogether() => SqlServerTestDatabase.RunAsync("quota_retry", async seed =>
    {
        seed.Add(new ResourceAccountingState { Id = 1, Ready = true });
        seed.Users.Add(new ApplicationUser { Id = "owner", UserName = "owner", Email = "owner@example.test" });
        await seed.SaveChangesAsync();
        var interceptor = new FailFirstCommit();
        await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>()
            .UseSqlServer(seed.Database.GetConnectionString(), sql => sql.EnableRetryOnFailure(1, TimeSpan.Zero, null))
            .AddInterceptors(interceptor).Options, Options.Create(new AbuseOptions { Quizzes = 1 }));
        db.Add(Quiz("retry", "Swedish")); await db.SaveChangesAsync();
        Assert.Equal(2, interceptor.Commits);
        Assert.Equal("retry", (await seed.Quizzes.SingleAsync()).Name);
        Assert.Equal(1, (await seed.Set<ResourceUsage>().SingleAsync(x => x.Scope == "owner" && x.Resource == "quizzes")).Used);
        Assert.Single(await seed.Set<ResourceEntry>().ToListAsync());
    });

    private sealed class FailFirstCommit : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
    {
        public int Commits;
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult> TransactionCommittingAsync(
            System.Data.Common.DbTransaction transaction, Microsoft.EntityFrameworkCore.Diagnostics.TransactionEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (++Commits == 1) throw new TimeoutException("Injected before commit.");
            return ValueTask.FromResult(result);
        }
    }

    [Theory]
    [InlineData("collections")]
    [InlineData("chats")]
    [InlineData("transcripts")]
    [InlineData("translations")]
    [InlineData("translation_sessions")]
    public async Task SavedResourceCountsRejectTheNextRecordAndPreserveExistingContent(string resource)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>().UseSqlite(connection).Options,
            Options.Create(new AbuseOptions { Collections = 1, Chats = 1, Transcripts = 1, Translations = 1, TranslationSessions = 1 }));
        await Initialize(db);
        var session = new SavedTranslationSession { UserId = "owner", ClientSessionId = Guid.NewGuid(), LanguageCode = "sv", Title = "session" };
        if (resource == "translations") { db.Add(session); await db.SaveChangesAsync(); }
        object Make(bool first) => resource switch
        {
            "collections" when first => new Collection { Id = Guid.NewGuid(), UserId = "owner", Name = "regular", Language = "Swedish" },
            "collections" => new AnkiCollection { Id = Guid.NewGuid(), UserId = "owner", Name = "anki", SourceLanguage = "English", TargetLanguage = "Swedish" },
            "chats" => new AssistantThread { Id = Guid.NewGuid(), UserId = "owner", Title = "chat" },
            "transcripts" => new RealtimeTranslationTranscript { UserId = "owner", Title = "transcript", TargetLanguage = "sv" },
            "translations" => new SavedTranslation { UserId = "owner", SessionId = session.Id, RequestId = Guid.NewGuid(), SourceLanguage = "en", TargetLanguage = "sv", SourceText = "hello", TranslatedText = "hej" },
            _ => new SavedTranslationSession { UserId = "owner", ClientSessionId = Guid.NewGuid(), LanguageCode = "sv", Title = "session" }
        };
        db.Add(Make(true)); await db.SaveChangesAsync();
        db.Add(Make(false));
        Assert.Equal(resource, (await Assert.ThrowsAsync<ResourceQuotaException>(() => db.SaveChangesAsync())).Resource);
        Assert.Equal(1, (await db.Set<ResourceUsage>().SingleAsync(x => x.Scope == "owner" && x.Resource == resource)).Used);
        Assert.Single(await db.Set<ResourceEntry>().Where(x => x.ChargesJson.Contains("\"" + resource + "\"")).ToListAsync());
    }

    [SqlServerFact]
    public Task TranscriptCapacityStopsSavingRetainsSegmentsAndReleasesUnusedReservation() => SqlServerTestDatabase.RunAsync("transcript_quota", async seed =>
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>().UseSqlServer(seed.Database.GetConnectionString()).Options;
        var limits = Options.Create(new AbuseOptions());
        await using var db = new GlosifyContext(options, limits); await Initialize(db);
        var transcript = new RealtimeTranslationTranscript { UserId = "owner", Title = "saved", TargetLanguage = "sv", Stream = RealtimeTranslationTranscriptStreams.Source };
        db.Add(transcript); await db.SaveChangesAsync();
        var quotas = new ResourceQuotaService(new Factory(options, limits), limits);
        var reserved = await quotas.ReserveAsync("owner", new() { ["content_bytes"] = 1500 });
        var session = new RealtimeTranslationSession { Id = Guid.NewGuid(), UserId = "owner", TargetLanguage = "sv",
            TranscriptId = transcript.Id, TranscriptConsentAt = DateTimeOffset.UtcNow, StorageReservationId = reserved,
            Model = "test", BillingModel = "test", Status = RealtimeTranslationSessionStatuses.Active, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) };
        db.Add(session); await db.SaveChangesAsync();
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var service = new Glosify.Services.RealtimeTranslation.RealtimeTranslationTranscriptService(db, TimeProvider.System, cache);
        await service.AppendAsync(session.Id, [new(1, "first", "kept", DateTimeOffset.UtcNow)]);
        await Assert.ThrowsAsync<ResourceQuotaException>(() => service.AppendAsync(session.Id, [new(2, "second", new string('x', 1000), DateTimeOffset.UtcNow)]));
        Assert.True((await db.RealtimeTranslationSessions.SingleAsync()).TranscriptStorageStopped);
        Assert.Empty(await db.Set<ResourceReservation>().ToListAsync());
        await service.AppendAsync(session.Id, [new(3, "third", "caption only", DateTimeOffset.UtcNow)]);
        Assert.Equal("kept", (await db.RealtimeTranslationTranscriptSegments.SingleAsync()).Text);
    });

    private sealed class Factory(DbContextOptions<GlosifyContext> settings, IOptions<AbuseOptions> limits) : IDbContextFactory<GlosifyContext>
    {
        public GlosifyContext CreateDbContext() => new(settings, limits);
        public Task<GlosifyContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private static Quiz Quiz(string name, string language) => new() { Id = Guid.NewGuid(), UserId = "owner", Name = name,
        SourceLanguage = "English", TargetLanguage = language, Language = language, ProcessingStatus = "Ready" };
    private static async Task Initialize(GlosifyContext db)
    {
        await db.Database.EnsureCreatedAsync();
        db.Add(new ResourceAccountingState { Id = 1, Ready = true });
        db.Users.Add(new ApplicationUser { Id = "owner", UserName = "owner", Email = "owner@example.test" });
        await db.SaveChangesAsync();
    }
}
