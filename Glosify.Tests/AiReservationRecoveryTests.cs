using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Abuse;
using Glosify.Services.Ai;
using Glosify.Services.Auth;
using Glosify.Services.RealtimeTranslation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Glosify.Tests;

public sealed class AiReservationRecoveryTests
{
    [Fact]
    public async Task RestartReleasesPreviousGenerationHoldsButKeepsCurrentWorkAndRecoverableEvaluations()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var factory = new Factory(options);
        var startedAt = DateTimeOffset.UtcNow;
        var clock = new FakeTimeProvider(startedAt.AddDays(-7));
        var usage = Options.Create(new AiUsageOptions
        {
            TrialGrantCredits = 0,
            MonthlyBudget = new() { Models = [new() { Deployment = "test", InputSekPerMillionTokens = 1, OutputSekPerMillionTokens = 1 }] },
        });
        AiCreditService Credits(GlosifyContext db) => new(db, factory, usage,
            new CreditPricingResolver(Options.Create(new CreditPricingOptions()), usage, Options.Create(new RealtimeTranslationOptions())),
            new TrialEligibilityService(db), clock);
        var abandoned = new List<Guid>();
        Guid evaluation, current, committed, released;
        await using (var db = factory.CreateDbContext())
        {
            db.Users.Add(new ApplicationUser { Id = "learner" });
            db.AiCreditAccounts.Add(new AiCreditAccount { UserId = "learner", BalanceCredits = 100 });
            await db.SaveChangesAsync();
            var credits = Credits(db);
            async Task<Guid> Reserve(string feature, string operation) =>
                (await credits.ReserveAsync(new("learner", feature, operation, Guid.NewGuid()), "openai", "test", 100000)).ReservationId;
            foreach (var feature in new[] { AiUsageFeatures.Assistant, AiUsageFeatures.TextTranslation,
                AiUsageFeatures.PageTranslation, AiUsageFeatures.ImageExtraction })
                abandoned.Add(await Reserve(feature, "generate"));
            evaluation = await Reserve(AiUsageFeatures.Assistant, "tool_use_evaluation");
            committed = await Reserve(AiUsageFeatures.Assistant, "generate");
            await credits.CommitUsageAsync(committed, new AiTokenUsage(100, 0, 0, 0, 100));
            released = await Reserve(AiUsageFeatures.Assistant, "generate");
            await credits.ReleaseAsync(released);
            clock.SetUtcNow(startedAt.AddSeconds(1));
            current = await Reserve(AiUsageFeatures.Assistant, "generate");
        }

        await using (var db = factory.CreateDbContext())
        {
            var services = new ServiceCollection();
            services.AddSingleton(db);
            services.AddSingleton(new ResourceQuotaService(factory, Options.Create(new AbuseOptions())));
            services.AddSingleton<IAiCreditService>(Credits(db));
            await using var provider = services.BuildServiceProvider();
            await ResourceMaintenanceService.CleanupAsync(provider, startedAt, default);
            await ResourceMaintenanceService.CleanupAsync(provider, startedAt, default);
        }

        await using var verify = factory.CreateDbContext();
        var account = await Credits(verify).GetOrCreateAccountAsync("learner");
        Assert.Equal(16, account.ReservedCredits);
        Assert.Equal(99.992m, account.BalanceCredits);
        Assert.Equal(83.992m, account.AvailableCredits);
        var ledger = await verify.AiCreditTransactions.ToListAsync();
        foreach (var id in abandoned)
            Assert.Single(ledger, t => t.ReservationId == id && t.Kind == AiCreditTransactionKinds.Release);
        Assert.DoesNotContain(ledger, t => (t.ReservationId == evaluation || t.ReservationId == current)
            && t.Kind == AiCreditTransactionKinds.Release);
        var retainedBudget = ledger.Where(t => (t.ReservationId == evaluation || t.ReservationId == current)
            && t.Kind == AiCreditTransactionKinds.Reservation).Sum(t => t.BudgetAmountMicros ?? 0);
        Assert.Equal(retainedBudget, (await verify.AiMonthlyBudgets.ToListAsync()).Sum(b => b.ReservedMicros));
        Assert.Single(ledger, t => t.Kind == AiCreditTransactionKinds.UsageDebit);
        Assert.Equal(7.992m, Assert.Single(ledger, t => t.ReservationId == committed && t.Kind == AiCreditTransactionKinds.Release).CreditAmount);
        Assert.Equal(8, Assert.Single(ledger, t => t.ReservationId == released && t.Kind == AiCreditTransactionKinds.Release).CreditAmount);
        Assert.Equal(6, ledger.Count(t => t.Kind == AiCreditTransactionKinds.Release));
    }

    private sealed class Factory(DbContextOptions<GlosifyContext> options) : IDbContextFactory<GlosifyContext>
    {
        public GlosifyContext CreateDbContext() => new(options);
    }
}
