using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Ai.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;
using Glosify.Services.Ai;

namespace Glosify.Tests;

public sealed class AiCreditServiceTests
{
    [Fact]
    public async Task GetOrCreateAccount_AppliesTrialGrantOnce()
    {
        await using var context = CreateContext();
        context.Users.Add(new ApplicationUser { Id = "user-1", Email = "user@example.test", UserName = "user@example.test" });
        await context.SaveChangesAsync();
        var service = CreateService(context);

        var first = await service.GetOrCreateAccountAsync("user-1");
        var second = await service.GetOrCreateAccountAsync("user-1");

        Assert.Equal(25, first.AvailableCredits);
        Assert.Equal(25, second.AvailableCredits);
        Assert.Single(await context.AiCreditTransactions.Where(t => t.Kind == AiCreditTransactionKinds.TrialGrant).ToListAsync());
    }

    [Fact]
    public async Task Grant_AddsCreditsAndWritesActorNote()
    {
        await using var context = CreateContext();
        context.Users.Add(new ApplicationUser { Id = "user-1", Email = "user@example.test", UserName = "user@example.test" });
        await context.SaveChangesAsync();
        var service = CreateService(context);

        await service.GrantAsync("admin-1", "user-1", 10, "Manual top-up");

        var account = await service.GetOrCreateAccountAsync("user-1");
        var grant = await context.AiCreditTransactions.SingleAsync(t => t.Kind == AiCreditTransactionKinds.AdminGrant);
        Assert.Equal(35, account.BalanceCredits);
        Assert.Equal("admin-1", grant.ActorUserId);
        Assert.Equal("Manual top-up", grant.Note);
    }

    [Fact]
    public async Task Reserve_BlocksWhenAvailableCreditsAreTooLow()
    {
        await using var context = CreateContext();
        context.Users.Add(new ApplicationUser { Id = "user-1", Email = "user@example.test", UserName = "user@example.test" });
        await context.SaveChangesAsync();
        var service = CreateService(context);

        var ex = await Assert.ThrowsAsync<InsufficientAiCreditsException>(() =>
            service.ReserveAsync(UsageContext("user-1"), "gemini", "test-model", 26_000));

        Assert.Equal(25, ex.AvailableCredits);
        Assert.Equal(26, ex.RequiredCredits);
    }

    [Fact]
    public async Task CommitUsage_DebitsRoundedCreditsAndReleasesUnusedReserve()
    {
        await using var context = CreateContext();
        context.Users.Add(new ApplicationUser { Id = "user-1", Email = "user@example.test", UserName = "user@example.test" });
        await context.SaveChangesAsync();
        var service = CreateService(context);

        var reservation = await service.ReserveAsync(UsageContext("user-1"), "gemini", "test-model", 2_500);
        await service.CommitUsageAsync(reservation.ReservationId, new AiTokenUsage(900, 200, 0, 0, 1_100));

        var account = await service.GetOrCreateAccountAsync("user-1");
        Assert.Equal(23, account.BalanceCredits);
        Assert.Equal(0, account.ReservedCredits);
        Assert.Equal(2, (await context.AiCreditTransactions.SingleAsync(t => t.Kind == AiCreditTransactionKinds.UsageDebit)).CreditAmount * -1);
        Assert.Single(await context.AiCreditTransactions.Where(t => t.Kind == AiCreditTransactionKinds.Release).ToListAsync());
    }

    [Fact]
    public async Task CommitUsage_AppliesTheConfiguredModelCreditMultiplier()
    {
        await using var context = CreateContext();
        context.Users.Add(new ApplicationUser { Id = "user-1", Email = "user@example.test", UserName = "user@example.test" });
        await context.SaveChangesAsync();
        var service = CreateService(context, creditMultiplier: 2m);

        var reservation = await service.ReserveAsync(
            UsageContext("user-1"),
            "foundry",
            "test-model",
            2_500);
        await service.CommitUsageAsync(
            reservation.ReservationId,
            new AiTokenUsage(900, 200, 0, 0, 1_100));

        var account = await service.GetOrCreateAccountAsync("user-1");
        Assert.Equal(21, account.BalanceCredits);
        Assert.Equal(0, account.ReservedCredits);
        var debit = await context.AiCreditTransactions
            .SingleAsync(transaction => transaction.Kind == AiCreditTransactionKinds.UsageDebit);
        Assert.Equal(-4, debit.CreditAmount);
        Assert.Single(await context.AiCreditTransactions
            .Where(transaction => transaction.Kind == AiCreditTransactionKinds.Release)
            .ToListAsync());
    }

    [Fact]
    public async Task Release_ReturnsReservedCreditsWithoutDebit()
    {
        await using var context = CreateContext();
        context.Users.Add(new ApplicationUser { Id = "user-1", Email = "user@example.test", UserName = "user@example.test" });
        await context.SaveChangesAsync();
        var service = CreateService(context);

        var reservation = await service.ReserveAsync(UsageContext("user-1"), "gemini", "test-model", 2_000);
        await service.ReleaseAsync(reservation.ReservationId);

        var account = await service.GetOrCreateAccountAsync("user-1");
        Assert.Equal(25, account.BalanceCredits);
        Assert.Equal(0, account.ReservedCredits);
        Assert.Empty(await context.AiCreditTransactions.Where(t => t.Kind == AiCreditTransactionKinds.UsageDebit).ToListAsync());
        Assert.Single(await context.AiCreditTransactions.Where(t => t.Kind == AiCreditTransactionKinds.Release).ToListAsync());
    }

    [Fact]
    public async Task MonthlyBudget_IsSharedAcrossUsersAndBlocksTheRequestThatWouldExceedIt()
    {
        await using var context = CreateContext();
        context.Users.AddRange(
            new ApplicationUser { Id = "user-1", Email = "one@example.test", UserName = "one@example.test" },
            new ApplicationUser { Id = "user-2", Email = "two@example.test", UserName = "two@example.test" });
        await context.SaveChangesAsync();
        var service = CreateService(
            context,
            monthlyLimitSek: 200m,
            inputSekPerMillionTokens: 1_000_000m,
            outputSekPerMillionTokens: 1_000_000m);

        await service.ReserveAsync(UsageContext("user-1"), "foundry", "test-model", 100);
        var exception = await Assert.ThrowsAsync<MonthlyAiBudgetExceededException>(() =>
            service.ReserveAsync(UsageContext("user-2"), "foundry", "test-model", 101));

        Assert.Equal("2026-07", exception.PeriodKey);
        Assert.Equal(200_000_000, exception.LimitMicros);
        Assert.Equal(100_000_000, exception.ReservedMicros);
        var budget = await context.AiMonthlyBudgets.SingleAsync();
        Assert.Equal(100_000_000, budget.AvailableMicros);
    }

    [Fact]
    public async Task MonthlyBudget_CommitReconcilesEstimateAgainstActualUsage()
    {
        await using var context = CreateContext();
        context.Users.Add(new ApplicationUser { Id = "user-1", Email = "user@example.test", UserName = "user@example.test" });
        await context.SaveChangesAsync();
        var service = CreateService(
            context,
            monthlyLimitSek: 200m,
            inputSekPerMillionTokens: 1_000_000m,
            outputSekPerMillionTokens: 1_000_000m);

        var reservation = await service.ReserveAsync(
            UsageContext("user-1"),
            "foundry",
            "test-model",
            100);
        await service.CommitUsageAsync(
            reservation.ReservationId,
            new AiTokenUsage(25, 25, 0, 0, 50));

        var budget = await context.AiMonthlyBudgets.SingleAsync();
        Assert.Equal(50_000_000, budget.SpentMicros);
        Assert.Equal(0, budget.ReservedMicros);
        Assert.Equal(150_000_000, budget.AvailableMicros);

        var debit = await context.AiCreditTransactions
            .SingleAsync(item => item.Kind == AiCreditTransactionKinds.UsageDebit);
        Assert.Equal(50_000_000, debit.BudgetAmountMicros);
        var release = await context.AiCreditTransactions
            .SingleAsync(item => item.Kind == AiCreditTransactionKinds.Release);
        Assert.Equal(50_000_000, release.BudgetAmountMicros);
    }

    [Fact]
    public async Task MonthlyBudget_ReleaseReturnsTheSharedReservation()
    {
        await using var context = CreateContext();
        context.Users.Add(new ApplicationUser { Id = "user-1", Email = "user@example.test", UserName = "user@example.test" });
        await context.SaveChangesAsync();
        var service = CreateService(
            context,
            monthlyLimitSek: 200m,
            inputSekPerMillionTokens: 1_000_000m,
            outputSekPerMillionTokens: 1_000_000m);

        var reservation = await service.ReserveAsync(
            UsageContext("user-1"),
            "foundry",
            "test-model",
            200);
        await service.ReleaseAsync(reservation.ReservationId);

        var budget = await context.AiMonthlyBudgets.SingleAsync();
        Assert.Equal(0, budget.SpentMicros);
        Assert.Equal(0, budget.ReservedMicros);
        Assert.Equal(200_000_000, budget.AvailableMicros);
    }

    [Fact]
    public async Task MonthlyBudget_UsesASeparateLedgerAfterTheStockholmMonthChanges()
    {
        await using var context = CreateContext();
        context.Users.Add(new ApplicationUser { Id = "user-1", Email = "user@example.test", UserName = "user@example.test" });
        await context.SaveChangesAsync();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 31, 21, 30, 0, TimeSpan.Zero));
        var service = CreateService(
            context,
            monthlyLimitSek: 200m,
            inputSekPerMillionTokens: 1_000_000m,
            outputSekPerMillionTokens: 1_000_000m,
            timeProvider: clock);

        await service.ReserveAsync(UsageContext("user-1"), "foundry", "test-model", 200);
        clock.Advance(TimeSpan.FromHours(1));
        await service.ReserveAsync(UsageContext("user-1"), "foundry", "test-model", 1);

        var budgets = await context.AiMonthlyBudgets
            .OrderBy(item => item.PeriodKey)
            .ToListAsync();
        Assert.Collection(
            budgets,
            july => Assert.Equal("2026-07", july.PeriodKey),
            august => Assert.Equal("2026-08", august.PeriodKey));
    }

    private static GlosifyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new GlosifyContext(options);
    }

    private static AiCreditService CreateService(
        GlosifyContext context,
        decimal creditMultiplier = 1m,
        decimal monthlyLimitSek = 200m,
        decimal inputSekPerMillionTokens = 1m,
        decimal outputSekPerMillionTokens = 1m,
        TimeProvider? timeProvider = null)
    {
        var generativeAiOptions = new GenerativeAiOptions
        {
            Foundry = new FoundryGenerativeAiOptions
            {
                AssistantDeployment = "test-model",
                AllowedAssistantDeployments = ["test-model"],
                AssistantModels =
                [
                    new AssistantModelOptions
                    {
                        Deployment = "test-model",
                        DisplayName = "Test Model",
                        Provider = "Test",
                        SpeedTier = "Test",
                        CostTier = "Test",
                        CreditMultiplier = creditMultiplier,
                    },
                ],
            },
        };
        var resolver = new GenerativeAiModelResolver(
            Options.Create(generativeAiOptions),
            Options.Create(new GeminiOptions()));
        return new AiCreditService(
            context,
            Options.Create(new AiUsageOptions
            {
                TrialGrantCredits = 25,
                CreditsPerThousandTokens = 1,
                MonthlyBudget = new AiMonthlyBudgetOptions
                {
                    Enabled = true,
                    LimitSek = monthlyLimitSek,
                    TimeZoneId = "Europe/Stockholm",
                    ReservationSafetyMultiplier = 1m,
                    Providers = ["foundry", "azure_ai_foundry"],
                    Models =
                    [
                        new AiModelPriceOptions
                        {
                            Deployment = "test-model",
                            InputSekPerMillionTokens = inputSekPerMillionTokens,
                            OutputSekPerMillionTokens = outputSekPerMillionTokens,
                        },
                    ],
                },
            }),
            resolver,
            timeProvider ?? new ManualTimeProvider(
                new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero)));
    }

    private static AiUsageContext UsageContext(string userId) =>
        new(userId, AiUsageFeatures.Assistant, "test", Guid.NewGuid());

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
