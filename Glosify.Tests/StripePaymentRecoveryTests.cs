using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai;
using Glosify.Services.Auth;
using Glosify.Services.Payments;
using Glosify.Services.RealtimeTranslation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class StripePaymentRecoveryTests
{
    [Fact]
    public async Task InterruptedCompletionRollsBackBeforeAnotherRefundAndRetry()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await VerifyAsync(fail => new SqlitePaymentContext(new DbContextOptionsBuilder<GlosifyContext>()
            .UseSqlite(connection).AddInterceptors(new FailPaidSave(fail)).Options));
    }

    [SqlServerFact]
    public Task SqlServerCompletionAndReconciliationCommitTogether() =>
        SqlServerTestDatabase.RunAsync("stripe_recovery", db => VerifyAsync(fail =>
            new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>()
                .UseSqlServer(db.Database.GetConnectionString()).AddInterceptors(new FailPaidSave(fail)).Options)));

    private static async Task VerifyAsync(Func<bool, GlosifyContext> create)
    {
        var purchase = new StripeCreditPurchase
        {
            Id = Guid.NewGuid(), UserId = "buyer", PackageKey = "starter", PriceId = "price",
            UnitAmountMinor = 5900, Currency = "sek", Credits = 100, DisplayName = "Starter",
            StripeCheckoutSessionId = "cs", StripePaymentIntentId = "pi",
        };
        var metadata = new Dictionary<string, string> { ["purchase_id"] = purchase.Id.ToString("D") };
        var factory = new ContextFactory(() => create(false));
        StripePaymentService Service(GlosifyContext db)
        {
            var usage = Options.Create(new AiUsageOptions { TrialGrantCredits = 0 });
            var credits = new AiCreditService(db, factory, usage,
                new CreditPricingResolver(Options.Create(new CreditPricingOptions()), usage,
                    Options.Create(new RealtimeTranslationOptions())), new TrialEligibilityService(db));
            return new StripePaymentService(db, credits,
                Options.Create(new StripeOptions { Enabled = true, SecretKey = "rk_test_example", WebhookSecret = "whsec_example" }),
                NullLogger<StripePaymentService>.Instance);
        }
        Task<StripePaymentConfirmation> Complete(GlosifyContext db) => Service(db).HandleCompletedCheckoutAsync(
            "completed", "cs", "paid", "pi", 5900, "sek", metadata);
        await using (var db = create(false))
        {
            await db.Database.EnsureCreatedAsync();
            db.Users.Add(new ApplicationUser { Id = "buyer", UserName = "buyer" });
            db.AiCreditAccounts.Add(new AiCreditAccount { UserId = "buyer" });
            db.StripeCreditPurchases.Add(purchase);
            await db.SaveChangesAsync();
            await Service(db).HandleRefundAsync("first-refund", "r1", "pi", 2950, "succeeded");
        }
        await using (var db = create(true))
            await Assert.ThrowsAsync<InterruptedCompletion>(() => Complete(db));

        await using (var db = create(false))
        {
            Assert.Equal(0, (await db.AiCreditAccounts.SingleAsync()).BalanceCredits);
            Assert.Empty(await db.AiCreditTransactions.ToListAsync());
            var pending = await db.StripeCreditPurchases.SingleAsync();
            Assert.Null(pending.PaidAt);
            Assert.Equal(50, pending.RevokedCredits);
            await Service(db).HandleRefundAsync("second-refund", "r2", "pi", 2950, "succeeded");
        }
        await using (var db = create(false))
            Assert.True((await Complete(db)).WasFulfilled);
        await using (var db = create(false))
            Assert.False((await Complete(db)).WasFulfilled);
        await using (var db = create(false))
        {
            var paid = await db.StripeCreditPurchases.SingleAsync();
            Assert.NotNull(paid.PaidAt);
            Assert.Equal(100, paid.RevokedCredits);
            Assert.Equal(StripeCreditPurchaseStatuses.Refunded, paid.Status);
            Assert.Equal(0, (await db.AiCreditAccounts.SingleAsync()).BalanceCredits);
            var ledger = await db.AiCreditTransactions.ToListAsync();
            Assert.Equal(100, Assert.Single(ledger, t => t.Kind == AiCreditTransactionKinds.StripePurchase).CreditAmount);
            Assert.Equal(-100, Assert.Single(ledger, t => t.Kind == AiCreditTransactionKinds.StripeAdjustment).CreditAmount);
        }
    }

    private sealed class InterruptedCompletion : Exception;

    private sealed class FailPaidSave(bool enabled) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var db = eventData.Context!;
            if (enabled && db.ChangeTracker.Entries<StripeCreditPurchase>().Any(e => e.Entity.PaidAt is not null))
            {
                Assert.Equal(2, db.ChangeTracker.Entries<AiCreditTransaction>().Count());
                throw new InterruptedCompletion();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ContextFactory(Func<GlosifyContext> create) : IDbContextFactory<GlosifyContext>
    {
        public GlosifyContext CreateDbContext() => create();
    }

    private sealed class SqlitePaymentContext(DbContextOptions<GlosifyContext> options) : GlosifyContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            // SQLite has transactions but no SQL Server-generated rowversion.
            builder.Entity<AiCreditAccount>().Property(x => x.RowVersion).ValueGeneratedNever();
            builder.Entity<StripeCreditPurchase>().Property(x => x.RowVersion).ValueGeneratedNever();
        }
    }
}
