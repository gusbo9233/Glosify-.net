using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Auth;
using Glosify.Services.RealtimeTranslation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

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
    public async Task GetOrCreateAccount_PasswordAccountStaysEligibleForALaterOauthLink()
    {
        await using var context = CreateContext();
        context.Users.Add(new ApplicationUser { Id = "user-1", Email = "user@example.test", UserName = "user@example.test" });
        await context.SaveChangesAsync();
        var eligibility = new MutableTrialEligibilityService();
        var service = CreateService(context, trialEligibility: eligibility);

        var passwordOnly = await service.GetOrCreateAccountAsync("user-1");
        Assert.Equal(0, passwordOnly.AvailableCredits);
        Assert.Null(passwordOnly.TrialGrantedAt);

        eligibility.IsEligible = true;
        var linked = await service.GetOrCreateAccountAsync("user-1");
        var repeated = await service.GetOrCreateAccountAsync("user-1");

        Assert.Equal(25, linked.AvailableCredits);
        Assert.Equal(25, repeated.AvailableCredits);
        Assert.NotNull(linked.TrialGrantedAt);
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
    public async Task StripePurchaseGrant_IsIdempotentAcrossWebhookRetries()
    {
        await using var context = CreateContext();
        context.Users.Add(new ApplicationUser { Id = "user-1", Email = "user@example.test", UserName = "user@example.test" });
        await context.SaveChangesAsync();
        var service = CreateService(context);

        var first = await service.GrantStripePurchaseAsync("user-1", "purchase-1", 10, "Stripe purchase");
        var second = await service.GrantStripePurchaseAsync("user-1", "purchase-1", 10, "Stripe purchase");

        var account = await service.GetOrCreateAccountAsync("user-1");
        Assert.True(first);
        Assert.False(second);
        Assert.Equal(35, account.BalanceCredits);
        Assert.Single(await context.AiCreditTransactions
            .Where(transaction => transaction.Kind == AiCreditTransactionKinds.StripePurchase)
            .ToListAsync());
    }

    [Fact]
    public async Task StripePaymentAdjustment_IsIdempotentAndCanRevokeSpentCredits()
    {
        await using var context = CreateContext();
        context.Users.Add(new ApplicationUser { Id = "user-1", Email = "user@example.test", UserName = "user@example.test" });
        await context.SaveChangesAsync();
        var service = CreateService(context);

        await service.GrantStripePurchaseAsync("user-1", "purchase-1", 10, "Stripe purchase");
        var first = await service.ApplyStripePaymentAdjustmentAsync(
            "user-1",
            "refund:re_1",
            -40,
            "Full refund");
        var second = await service.ApplyStripePaymentAdjustmentAsync(
            "user-1",
            "refund:re_1",
            -40,
            "Full refund");

        var account = await service.GetOrCreateAccountAsync("user-1");
        Assert.True(first);
        Assert.False(second);
        Assert.Equal(-5, account.BalanceCredits);
        var adjustment = await context.AiCreditTransactions.SingleAsync(transaction =>
            transaction.Kind == AiCreditTransactionKinds.StripeAdjustment);
        Assert.Equal(-40, adjustment.CreditAmount);
    }

    [Fact]
    public async Task Reserve_BlocksWhenAvailableCreditsAreTooLow()
    {
        await using var context = CreateContext();
        context.Users.Add(new ApplicationUser { Id = "user-1", Email = "user@example.test", UserName = "user@example.test" });
        await context.SaveChangesAsync();
        var service = CreateService(context);

        var ex = await Assert.ThrowsAsync<InsufficientAiCreditsException>(() =>
            service.ReserveAsync(UsageContext("user-1"), "openai", "test-model", 26_000));

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

        var reservation = await service.ReserveAsync(UsageContext("user-1"), "openai", "test-model", 2_500);
        await service.CommitUsageAsync(reservation.ReservationId, new AiTokenUsage(900, 200, 0, 0, 1_100));

        var account = await service.GetOrCreateAccountAsync("user-1");
        Assert.Equal(23, account.BalanceCredits);
        Assert.Equal(0, account.ReservedCredits);
        Assert.Equal(2, (await context.AiCreditTransactions.SingleAsync(t => t.Kind == AiCreditTransactionKinds.UsageDebit)).CreditAmount * -1);
        Assert.Single(await context.AiCreditTransactions.Where(t => t.Kind == AiCreditTransactionKinds.Release).ToListAsync());
    }

    [Fact]
    public async Task CommitUsage_UsesTheSingleLunaCreditMultiplier()
    {
        await using var context = CreateContext();
        context.Users.Add(new ApplicationUser { Id = "user-1", Email = "user@example.test", UserName = "user@example.test" });
        await context.SaveChangesAsync();
        var service = CreateService(context);

        var reservation = await service.ReserveAsync(
            UsageContext("user-1"),
            "openai",
            "test-model",
            2_500);
        await service.CommitUsageAsync(
            reservation.ReservationId,
            new AiTokenUsage(900, 200, 0, 0, 1_100));

        var account = await service.GetOrCreateAccountAsync("user-1");
        Assert.Equal(23, account.BalanceCredits);
        Assert.Equal(0, account.ReservedCredits);
        var debit = await context.AiCreditTransactions
            .SingleAsync(transaction => transaction.Kind == AiCreditTransactionKinds.UsageDebit);
        Assert.Equal(-2, debit.CreditAmount);
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

        var reservation = await service.ReserveAsync(UsageContext("user-1"), "openai", "test-model", 2_000);
        await service.ReleaseAsync(reservation.ReservationId);

        var account = await service.GetOrCreateAccountAsync("user-1");
        Assert.Equal(25, account.BalanceCredits);
        Assert.Equal(0, account.ReservedCredits);
        Assert.Empty(await context.AiCreditTransactions.Where(t => t.Kind == AiCreditTransactionKinds.UsageDebit).ToListAsync());
        Assert.Single(await context.AiCreditTransactions.Where(t => t.Kind == AiCreditTransactionKinds.Release).ToListAsync());
    }

    [Fact]
    public async Task CommitUsageIndependently_DropsUncertainTrackedState_ChargesUsageAndIsIdempotent()
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
            "openai",
            "test-model",
            100);

        // Model the state left behind when the request-scoped SaveChanges fails after
        // CommitUsageCoreAsync has already mutated its tracked entities.
        var uncertainAccount = await context.AiCreditAccounts.SingleAsync();
        uncertainAccount.BalanceCredits = -999;
        uncertainAccount.ReservedCredits = 999;
        var uncertainBudget = await context.AiMonthlyBudgets.SingleAsync();
        uncertainBudget.SpentMicros = 199_000_000;
        context.AiCreditTransactions.Add(new AiCreditTransaction
        {
            Id = Guid.NewGuid(),
            UserId = "user-1",
            ReservationId = reservation.ReservationId,
            Kind = AiCreditTransactionKinds.UsageDebit,
            CreditAmount = -999,
        });

        await service.CommitUsageIndependentlyAsync(
            reservation.ReservationId,
            new AiTokenUsage(25, 25, 0, 0, 50));
        await service.CommitUsageIndependentlyAsync(
            reservation.ReservationId,
            new AiTokenUsage(25, 25, 0, 0, 50));

        var account = await service.GetOrCreateAccountAsync("user-1");
        Assert.Equal(24, account.BalanceCredits);
        Assert.Equal(0, account.ReservedCredits);

        var budget = await context.AiMonthlyBudgets.SingleAsync();
        Assert.Equal(50_000_000, budget.SpentMicros);
        Assert.Equal(0, budget.ReservedMicros);

        var usageDebit = await context.AiCreditTransactions
            .SingleAsync(transaction => transaction.Kind == AiCreditTransactionKinds.UsageDebit);
        Assert.Equal(-1, usageDebit.CreditAmount);
        Assert.Equal(50, usageDebit.TotalTokens);
        Assert.Equal(50_000_000, usageDebit.BudgetAmountMicros);
        var release = await context.AiCreditTransactions
            .SingleAsync(transaction => transaction.Kind == AiCreditTransactionKinds.Release);
        Assert.Equal(0, release.CreditAmount);
        Assert.Equal(50_000_000, release.BudgetAmountMicros);
    }

    [Fact]
    public async Task Reservation_debit_and_release_keep_turn_and_invocation_correlation()
    {
        await using var context = CreateContext();
        context.Users.Add(new ApplicationUser { Id = "user-1", Email = "user@example.test", UserName = "user@example.test" });
        await context.SaveChangesAsync();
        var service = CreateService(context);
        var turnId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();
        var usage = new AiUsageContext(
            "user-1",
            AiUsageFeatures.Assistant,
            "assistant_turn",
            invocationId,
            "assistant_thread",
            Guid.NewGuid().ToString(),
            turnId);

        var reservation = await service.ReserveAsync(usage, "openai", "test-model", 2_500);
        await service.CommitUsageAsync(reservation.ReservationId, new AiTokenUsage(900, 200, 0, 0, 1_100));

        var correlated = await context.AiCreditTransactions
            .Where(transaction => transaction.ReservationId == reservation.ReservationId)
            .ToListAsync();
        Assert.NotEmpty(correlated);
        Assert.All(correlated, transaction => Assert.Equal(invocationId, transaction.OperationId));
        Assert.All(correlated, transaction => Assert.Equal(turnId, transaction.AssistantTurnId));
        var debit = Assert.Single(correlated, transaction => transaction.Kind == AiCreditTransactionKinds.UsageDebit);
        Assert.True(debit.BudgetAmountMicros > 0);
        Assert.Single(correlated, transaction => transaction.Kind == AiCreditTransactionKinds.Release);
    }

    [Fact]
    public async Task DurationUsage_ChargesEightCreditsPerStartedMinute()
    {
        await using var context = CreateContext();
        context.Users.Add(new ApplicationUser { Id = "user-1", Email = "user@example.test", UserName = "user@example.test" });
        await context.SaveChangesAsync();
        var service = CreateService(context, audioSekPerMinute: 1m);

        for (var minute = 1; minute <= 3; minute++)
        {
            var reservation = await service.ReserveDurationAsync(
                new AiUsageContext("user-1", AiUsageFeatures.RealtimeTranslation, "subtitle_minute", Guid.NewGuid()),
                "openai",
                "test-model",
                60,
                8);
            await service.CommitDurationUsageAsync(reservation.ReservationId, 60);
        }

        var account = await service.GetOrCreateAccountAsync("user-1");
        Assert.Equal(1, account.AvailableCredits);
        var exception = await Assert.ThrowsAsync<InsufficientAiCreditsException>(() =>
            service.ReserveDurationAsync(
                new AiUsageContext("user-1", AiUsageFeatures.RealtimeTranslation, "subtitle_minute", Guid.NewGuid()),
                "openai",
                "test-model",
                60,
                8));
        Assert.Equal(1, exception.AvailableCredits);
        Assert.Equal(8, exception.RequiredCredits);
        Assert.Equal(3, await context.AiCreditTransactions.CountAsync(transaction =>
            transaction.Kind == AiCreditTransactionKinds.UsageDebit
            && transaction.AudioDurationSeconds == 60));
    }

    [Fact]
    public async Task DurationReservation_ReleaseReturnsCreditsAndBudget()
    {
        await using var context = CreateContext();
        context.Users.Add(new ApplicationUser { Id = "user-1", Email = "user@example.test", UserName = "user@example.test" });
        await context.SaveChangesAsync();
        var service = CreateService(context, audioSekPerMinute: 1m);

        var reservation = await service.ReserveDurationAsync(
            new AiUsageContext("user-1", AiUsageFeatures.RealtimeTranslation, "subtitle_minute", Guid.NewGuid()),
            "openai",
            "test-model",
            60,
            8);
        await service.ReleaseAsync(reservation.ReservationId);

        var account = await service.GetOrCreateAccountAsync("user-1");
        var budget = await context.AiMonthlyBudgets.SingleAsync();
        Assert.Equal(25, account.AvailableCredits);
        Assert.Equal(0, budget.ReservedMicros);
        Assert.Equal(0, budget.SpentMicros);
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

        await service.ReserveAsync(UsageContext("user-1"), "openai", "test-model", 100);
        var callerQuiz = new Quiz
        {
            Id = Guid.NewGuid(),
            Name = "Caller work survives",
            UserId = "user-2",
            CreatedAt = DateTimeOffset.UtcNow,
            ProcessingStatus = "ready",
            SourceLanguage = "en",
            TargetLanguage = "sv",
            Language = "sv",
        };
        context.Quizzes.Add(callerQuiz);
        var exception = await Assert.ThrowsAsync<MonthlyAiBudgetExceededException>(() =>
            service.ReserveAsync(UsageContext("user-2"), "openai", "test-model", 101));

        Assert.Equal("2026-07", exception.PeriodKey);
        Assert.Equal(200_000_000, exception.LimitMicros);
        Assert.Equal(100_000_000, exception.ReservedMicros);
        Assert.Equal(EntityState.Added, context.Entry(callerQuiz).State);
        await context.SaveChangesAsync();
        var budget = await context.AiMonthlyBudgets.SingleAsync();
        Assert.Equal(100_000_000, budget.AvailableMicros);
        Assert.NotNull(budget.ExhaustedAt);
        Assert.Equal(PaidServiceGate.BudgetExhaustedReason, budget.ExhaustedReason);
        Assert.Equal("Caller work survives", (await context.Quizzes.SingleAsync(
            quiz => quiz.Id == callerQuiz.Id)).Name);
        Assert.Null(await context.AiCreditAccounts.SingleOrDefaultAsync(
            account => account.UserId == "user-2"));
        Assert.Empty(await context.AiCreditTransactions
            .Where(transaction => transaction.UserId == "user-2")
            .ToListAsync());

        var repeated = await Assert.ThrowsAsync<MonthlyAiBudgetExceededException>(() =>
            service.ReserveAsync(UsageContext("user-2"), "openai", "test-model", 1));
        Assert.Equal(new DateTimeOffset(2026, 7, 31, 22, 0, 0, TimeSpan.Zero), repeated.ResetsAtUtc);
        await context.SaveChangesAsync();
        Assert.Null(await context.AiCreditAccounts.SingleOrDefaultAsync(
            account => account.UserId == "user-2"));
        Assert.Empty(await context.AiCreditTransactions
            .Where(transaction => transaction.UserId == "user-2")
            .ToListAsync());
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
            "openai",
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
    public async Task MonthlyBudget_RecordsModeledOverrunWithoutLettingTheEnforcementLedgerExceedItsLimit()
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
            "openai",
            "test-model",
            100);
        await service.CommitUsageAsync(
            reservation.ReservationId,
            new AiTokenUsage(125, 125, 0, 0, 250));

        var budget = await context.AiMonthlyBudgets.SingleAsync();
        Assert.Equal(200_000_000, budget.SpentMicros);
        Assert.Equal(50_000_000, budget.OverrunMicros);
        Assert.Equal(0, budget.AvailableMicros);
        Assert.NotNull(budget.ExhaustedAt);
    }

    [Fact]
    public async Task MonthlyBudget_CommitUsesALoweredConfiguredLimit()
    {
        await using var context = CreateContext();
        context.Users.Add(new ApplicationUser { Id = "user-1", Email = "user@example.test", UserName = "user@example.test" });
        await context.SaveChangesAsync();
        var options = CreateUsageOptions(
            monthlyLimitSek: 200m,
            inputSekPerMillionTokens: 1_000_000m,
            outputSekPerMillionTokens: 1_000_000m);
        var service = CreateService(context, usageOptions: options);

        var reservation = await service.ReserveAsync(
            UsageContext("user-1"),
            "openai",
            "test-model",
            100);
        options.MonthlyBudget.LimitSek = 75m;
        await service.CommitUsageAsync(
            reservation.ReservationId,
            new AiTokenUsage(50, 50, 0, 0, 100));

        var budget = await context.AiMonthlyBudgets.SingleAsync();
        Assert.Equal(75_000_000, budget.LimitMicros);
        Assert.Equal(75_000_000, budget.SpentMicros);
        Assert.Equal(25_000_000, budget.OverrunMicros);
        Assert.Equal(0, budget.AvailableMicros);
        Assert.NotNull(budget.ExhaustedAt);
    }

    [Fact]
    public async Task MonthlyBudget_DurationCommitUsesALoweredConfiguredLimit()
    {
        await using var context = CreateContext();
        context.Users.Add(new ApplicationUser { Id = "user-1", Email = "user@example.test", UserName = "user@example.test" });
        await context.SaveChangesAsync();
        var options = CreateUsageOptions(
            monthlyLimitSek: 200m,
            inputSekPerMillionTokens: 1m,
            outputSekPerMillionTokens: 1m,
            audioSekPerMinute: 100m);
        var service = CreateService(context, usageOptions: options);

        var reservation = await service.ReserveDurationAsync(
            new AiUsageContext("user-1", AiUsageFeatures.RealtimeTranslation, "subtitle_minute", Guid.NewGuid()),
            "openai",
            "test-model",
            60,
            8);
        options.MonthlyBudget.LimitSek = 75m;
        await service.CommitDurationUsageAsync(reservation.ReservationId, 60);

        var budget = await context.AiMonthlyBudgets.SingleAsync();
        Assert.Equal(75_000_000, budget.LimitMicros);
        Assert.Equal(75_000_000, budget.SpentMicros);
        Assert.Equal(25_000_000, budget.OverrunMicros);
        Assert.Equal(0, budget.AvailableMicros);
        Assert.NotNull(budget.ExhaustedAt);
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
            "openai",
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

        await service.ReserveAsync(UsageContext("user-1"), "openai", "test-model", 200);
        clock.Advance(TimeSpan.FromHours(1));
        await service.ReserveAsync(UsageContext("user-1"), "openai", "test-model", 1);

        var budgets = await context.AiMonthlyBudgets
            .OrderBy(item => item.PeriodKey)
            .ToListAsync();
        Assert.Collection(
            budgets,
            july => Assert.Equal("2026-07", july.PeriodKey),
            august => Assert.Equal("2026-08", august.PeriodKey));
    }

    [Fact]
    public async Task Concurrency_retry_detaches_only_the_credit_entities()
    {
        // The service is registered scoped and shares the request's GlosifyContext, so the
        // ChangeTracker.Clear() this replaced detached whatever the caller was tracking too.
        // Callers then mutated detached objects and their SaveChangesAsync silently wrote
        // nothing: RealtimeTranslationService.BeginMinuteAsync holds a tracked session and
        // minute across CommitDurationUsageAsync, so a retry there charged the user for a
        // subtitle minute the session never recorded.
        //
        // This exercises the detach seam directly rather than racing a real conflict,
        // because AiCreditAccount.RowVersion is IsRowVersion() — store-generated on SQL
        // Server and simply absent on SQLite, so the retry path is not reachable end-to-end
        // against either test provider.
        await using var context = CreateContext();
        context.Users.Add(new ApplicationUser { Id = "user-1", Email = "user@example.test", UserName = "user@example.test" });
        await context.SaveChangesAsync();
        var service = CreateService(context);

        // Establish the account, then give the caller an unsaved edit of its own.
        await service.GetOrCreateAccountAsync("user-1");
        var account = await context.AiCreditAccounts.SingleAsync(a => a.UserId == "user-1");
        account.BalanceCredits = 999;

        var quiz = new Quiz
        {
            Id = Guid.NewGuid(),
            Name = "Original name",
            UserId = "user-1",
            CreatedAt = DateTimeOffset.UtcNow,
            ProcessingStatus = "ready",
            SourceLanguage = "en",
            TargetLanguage = "sv",
            Language = "sv",
        };
        context.Quizzes.Add(quiz);

        Assert.Equal(EntityState.Modified, context.Entry(account).State);
        Assert.Equal(EntityState.Added, context.Entry(quiz).State);

        service.DetachCreditEntities();

        // The credit row is dropped so the next attempt re-reads it...
        Assert.Equal(EntityState.Detached, context.Entry(account).State);
        // ...while the caller's pending work is untouched and still saves.
        Assert.Equal(EntityState.Added, context.Entry(quiz).State);

        await context.SaveChangesAsync();
        Assert.Equal("Original name", (await context.Quizzes.SingleAsync(q => q.Id == quiz.Id)).Name);
    }

    private static GlosifyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new FactoryBackedGlosifyContext(options);
    }

    private static AiCreditService CreateService(
        GlosifyContext context,
        decimal monthlyLimitSek = 200m,
        decimal inputSekPerMillionTokens = 1m,
        decimal outputSekPerMillionTokens = 1m,
        decimal? audioSekPerMinute = null,
        TimeProvider? timeProvider = null,
        ITrialEligibilityService? trialEligibility = null,
        AiUsageOptions? usageOptions = null)
    {
        var effectiveUsageOptions = usageOptions ?? CreateUsageOptions(
            monthlyLimitSek,
            inputSekPerMillionTokens,
            outputSekPerMillionTokens,
            audioSekPerMinute);
        var pricing = new CreditPricingResolver(
            Options.Create(new CreditPricingOptions()),
            Options.Create(effectiveUsageOptions),
            Options.Create(new RealtimeTranslationOptions()));
        return new AiCreditService(
            context,
            new TestDbContextFactory(context),
            Options.Create(effectiveUsageOptions),
            pricing,
            trialEligibility ?? new MutableTrialEligibilityService { IsEligible = true },
            timeProvider ?? new ManualTimeProvider(
                new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero)));
    }

    private static AiUsageOptions CreateUsageOptions(
        decimal monthlyLimitSek,
        decimal inputSekPerMillionTokens,
        decimal outputSekPerMillionTokens,
        decimal? audioSekPerMinute = null) =>
        new()
        {
            TrialGrantCredits = 25,
            CreditsPerThousandTokens = 1,
            MonthlyBudget = new AiMonthlyBudgetOptions
            {
                Enabled = true,
                LimitSek = monthlyLimitSek,
                TimeZoneId = "Europe/Stockholm",
                ReservationSafetyMultiplier = 1m,
                Providers = ["openai"],
                Models =
                [
                    new AiModelPriceOptions
                    {
                        Deployment = "test-model",
                        InputSekPerMillionTokens = inputSekPerMillionTokens,
                        OutputSekPerMillionTokens = outputSekPerMillionTokens,
                        AudioSekPerMinute = audioSekPerMinute,
                    },
                ],
            },
        };

    private static AiUsageContext UsageContext(string userId) =>
        new(userId, AiUsageFeatures.Assistant, "test", Guid.NewGuid());

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class MutableTrialEligibilityService : ITrialEligibilityService
    {
        public bool IsEligible { get; set; }

        public Task<bool> IsEligibleAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(IsEligible);
    }
}
