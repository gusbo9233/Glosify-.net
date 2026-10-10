using Glosify.Data;
using Glosify.Services.Abuse;
using Glosify.Models.Entities;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Avatar;
using Glosify.Services.RealtimeTranslation;
using Glosify.Services.Speech;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Glosify.Tests;

public sealed class AvatarBillingTests
{
    [Fact]
    public async Task ChargesOnlySubmittedAudioAndReleasesTheRestExactlyOnce()
    {
        var fixture = new BillingFixture(); await fixture.Seed(10);
        var id = await fixture.Billing.ReserveAsync("admin", fixture.Session, "recognition", 45, default);
        await fixture.Billing.SubmittedAsync(id, 1.2m, default);
        await fixture.Billing.SubmittedAsync(id, 1.2m, default);
        await fixture.Billing.SettleAsync(id, default); await fixture.Billing.SettleAsync(id, default);
        await using var db = fixture.Factory.CreateDbContext();
        var account = await db.AiCreditAccounts.SingleAsync();
        Assert.Equal(9.94m, account.BalanceCredits); Assert.Equal(0, account.ReservedCredits);
        var debit = Assert.Single(await db.AiCreditTransactions.Where(x => x.Kind == AiCreditTransactionKinds.UsageDebit).ToListAsync());
        Assert.Equal(-.06m, debit.CreditAmount); Assert.Equal("avatar.recognition", debit.Operation);
        Assert.Equal(fixture.Session.ToString(), debit.RelatedEntityId);
        Assert.Equal(0, (await db.AiMonthlyBudgets.SingleAsync()).ReservedMicros);
    }

    [Fact]
    public async Task FailureBeforeSubmissionCostsNothingAndExpiredSubmittedUsageIsRecovered()
    {
        var fixture = new BillingFixture(); await fixture.Seed(100);
        var empty = await fixture.Billing.ReserveAsync("admin", fixture.Session, "speech", 100, default);
        await fixture.Billing.SettleAsync(empty, default);
        var used = await fixture.Billing.ReserveAsync("admin", fixture.Session, "speech", 200, default);
        await fixture.Billing.SubmittedAsync(used, 40, default);
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        await fixture.Billing.RecoverAsync(default); await fixture.Billing.RecoverAsync(default);
        await using var db = fixture.Factory.CreateDbContext();
        var account = await db.AiCreditAccounts.SingleAsync();
        Assert.Equal(100 - new SpeechOptions().CalculateCredits(40), account.BalanceCredits);
        Assert.Equal(0, account.ReservedCredits);
        Assert.Equal(2, await db.Set<AvatarUsageOperation>().CountAsync(x => x.Settled));
    }

    [Fact]
    public async Task InsufficientCreditAndExceededReservationCannotSpend()
    {
        var fixture = new BillingFixture(); await fixture.Seed(1);
        await Assert.ThrowsAsync<InsufficientAiCreditsException>(() => fixture.Billing.ReserveAsync("admin", fixture.Session, "recognition", 45, default));
        var id = await fixture.Billing.ReserveAsync("admin", fixture.Session, "recognition", 10, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Billing.SubmittedAsync(id, 11, default));
        await fixture.Billing.SettleAsync(id, default);
        await using var db = fixture.Factory.CreateDbContext();
        var account = await db.AiCreditAccounts.SingleAsync();
        Assert.Equal(1, account.AvailableCredits);
        Assert.Empty(await db.AiCreditTransactions.Where(x => x.Kind == AiCreditTransactionKinds.UsageDebit).ToListAsync());
    }

    [SqlServerFact]
    public Task ConcurrentReservationsAndSettlementCannotDoubleSpend() => SqlServerTestDatabase.RunAsync("avatar_billing", async db =>
    {
        db.Users.Add(new ApplicationUser { Id = "admin", UserName = "admin" });
        db.AiCreditAccounts.Add(new AiCreditAccount { UserId = "admin", BalanceCredits = .5m });
        await db.SaveChangesAsync();
        var fixture = new BillingFixture(new Factory(db.Database.GetConnectionString()!));
        async Task<Guid?> Reserve()
        {
            try { return await fixture.Billing.ReserveAsync("admin", fixture.Session, "recognition", 10, default); }
            catch (InsufficientAiCreditsException) { return null; }
        }
        var attempts = await Task.WhenAll(Reserve(), Reserve());
        var id = Assert.Single(attempts, x => x.HasValue)!.Value;
        Assert.Single(attempts, x => !x.HasValue);
        await fixture.Billing.SubmittedAsync(id, 2, default);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.Billing.SettleAsync(id, default)));
        db.ChangeTracker.Clear();
        var account = await db.AiCreditAccounts.SingleAsync();
        Assert.Equal(.4m, account.BalanceCredits); Assert.Equal(0, account.ReservedCredits);
        Assert.Single(await db.AiCreditTransactions.Where(x => x.Kind == AiCreditTransactionKinds.UsageDebit).ToListAsync());
        Assert.Equal(0, (await db.AiMonthlyBudgets.SingleAsync()).ReservedMicros);
    });

    [SqlServerFact]
    public Task OperationLockProtectsSettlementWithoutBlockingOtherSessions() => SqlServerTestDatabase.RunAsync("avatar_locks", async db =>
    {
        db.Users.Add(new ApplicationUser { Id = "admin", UserName = "admin" });
        db.AiCreditAccounts.Add(new AiCreditAccount { UserId = "admin", BalanceCredits = 10 });
        await db.SaveChangesAsync();
        var fixture = new BillingFixture(new Factory(db.Database.GetConnectionString()!));
        var first = await fixture.Billing.ReserveAsync("admin", fixture.Session, "recognition", 10, default);
        var second = await fixture.Billing.ReserveAsync("admin", Guid.NewGuid(), "recognition", 10, default);
        await using var transaction = await db.Database.BeginTransactionAsync();
        // Simulate a slow operation, plus an older deployment still holding the
        // former shared lock. Neither may delay another operation's audio frames.
        await ResourceAccounting.LockAsync(db, "glosify:avatar-billing", default);
        await ResourceAccounting.LockAsync(db, $"glosify:avatar-billing:{first:N}", default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var submit = fixture.Billing.SubmittedAsync(first, 2, timeout.Token);
        var settle = fixture.Billing.SettleAsync(first, timeout.Token);
        await fixture.Billing.SubmittedAsync(second, 3, timeout.Token);
        Assert.False(submit.IsCompleted);
        Assert.False(settle.IsCompleted);
        await transaction.CommitAsync();
        // Either submission wins (exactly 2 s billed) or settlement wins (no
        // submission is allowed afterwards). There must never be a lost debit.
        try { await submit; } catch (InvalidOperationException) { }
        await settle;
        await fixture.Billing.SettleAsync(second, timeout.Token);
        db.ChangeTracker.Clear();
        var operation = await db.Set<AvatarUsageOperation>().SingleAsync(x => x.Id == first);
        Assert.True(operation.Settled);
        Assert.Contains(operation.SubmittedUnits, new[] { 0m, 2m });
        var account = await db.AiCreditAccounts.SingleAsync();
        Assert.Equal(10m - (operation.SubmittedUnits + 3m) * .05m, account.BalanceCredits);
        Assert.Equal(0, account.ReservedCredits);
        Assert.Equal(0, (await db.AiMonthlyBudgets.SingleAsync()).ReservedMicros);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Billing.SubmittedAsync(first, 3, default));
    });

    [Fact]
    public async Task MonthlyBudgetFailureLeavesUserCreditsUnreserved()
    {
        var fixture = new BillingFixture(); await fixture.Seed(10);
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var local = TimeZoneInfo.ConvertTime(fixture.Clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm"));
            db.AiMonthlyBudgets.Add(new AiMonthlyBudget { PeriodKey = local.ToString("yyyy-MM"), SpentMicros = 300_000_000 });
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<PaidServicesBudgetExhaustedException>(() => fixture.Billing.ReserveAsync("admin", fixture.Session, "recognition", 10, default));
        await using var verify = fixture.Factory.CreateDbContext();
        Assert.Equal(10, (await verify.AiCreditAccounts.SingleAsync()).AvailableCredits);
        Assert.Empty(await verify.Set<AvatarUsageOperation>().ToListAsync());
    }

    private sealed class BillingFixture
    {
        public FakeTimeProvider Clock { get; } = new();
        public Factory Factory { get; }
        public Guid Session { get; } = Guid.NewGuid();
        public AvatarBilling Billing { get; }
        public BillingFixture(Factory? factory = null)
        {
            Factory = factory ?? new();
            var usage = Options.Create(new AiUsageOptions { MonthlyBudget = new() { Enabled = true, LimitSek = 300,
                Models = [new() { Deployment = "elevenlabs-scribe-v2-realtime", AudioSekPerMinute = .35m }] } });
            var resolver = new CreditPricingResolver(Options.Create(new CreditPricingOptions { Subtitles = new() { ScribeCreditsPerStartedMinute = 3 } }),
                usage, Options.Create(new RealtimeTranslationOptions()));
            var pricing = new AvatarPricing(resolver, Options.Create(new SpeechOptions()), Options.Create(new AvatarOptions()), usage, Options.Create(new GenerativeAiOptions()));
            Billing = new AvatarBilling(Factory, pricing, usage, Clock);
        }
        public async Task Seed(decimal balance)
        {
            await using var db = Factory.CreateDbContext();
            db.AiCreditAccounts.Add(new AiCreditAccount { UserId = "admin", BalanceCredits = balance });
            await db.SaveChangesAsync();
        }
    }
    private sealed class Factory : IDbContextFactory<GlosifyContext>
    {
        private readonly DbContextOptions<GlosifyContext> _options;
        public Factory(string? connection = null) => _options = connection is null
            ? new DbContextOptionsBuilder<GlosifyContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options
            : new DbContextOptionsBuilder<GlosifyContext>().UseSqlServer(connection).Options;
        public GlosifyContext CreateDbContext() => new(_options);
        public Task<GlosifyContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
