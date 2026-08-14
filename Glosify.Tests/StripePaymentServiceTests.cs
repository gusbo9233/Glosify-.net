using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai;
using Glosify.Services.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class StripePaymentServiceTests
{
    [Fact]
    public async Task Refund_IsProportionalAndIdempotent()
    {
        await using var context = CreateContext();
        context.StripeCreditPurchases.Add(PaidPurchase());
        await context.SaveChangesAsync();
        var credits = new RecordingCreditService();
        var service = CreateService(context, credits);

        await service.HandleRefundAsync("evt_1", "re_1", "pi_1", 2_950, "succeeded");
        await service.HandleRefundAsync("evt_retry", "re_1", "pi_1", 2_950, "succeeded");

        var purchase = await context.StripeCreditPurchases.SingleAsync();
        Assert.Equal(2_950, purchase.RefundedAmountMinor);
        Assert.Equal(50, purchase.RevokedCredits);
        Assert.Equal(StripeCreditPurchaseStatuses.PartiallyRefunded, purchase.Status);
        var adjustment = Assert.Single(credits.Adjustments);
        Assert.StartsWith("stripe:", adjustment.Id, StringComparison.Ordinal);
        Assert.Equal(-50, adjustment.Delta);
        Assert.Single(await context.StripePaymentEvents.ToListAsync());
    }

    [Fact]
    public async Task WonDispute_RestoresOnlyCreditsNotAlreadyRefunded()
    {
        await using var context = CreateContext();
        var purchase = PaidPurchase();
        purchase.RefundedAmountMinor = 1_180;
        purchase.RevokedCredits = 20;
        purchase.Status = StripeCreditPurchaseStatuses.PartiallyRefunded;
        context.StripeCreditPurchases.Add(purchase);
        await context.SaveChangesAsync();
        var credits = new RecordingCreditService();
        var service = CreateService(context, credits);

        await service.HandleDisputeAsync(
            "evt_open",
            "dp_1",
            "pi_1",
            "charge.dispute.created",
            "needs_response");
        await service.HandleDisputeAsync(
            "evt_won",
            "dp_1",
            "pi_1",
            "charge.dispute.closed",
            "won");

        await context.Entry(purchase).ReloadAsync();
        Assert.False(purchase.HasUnresolvedDispute);
        Assert.Equal(20, purchase.RevokedCredits);
        Assert.Equal(StripeCreditPurchaseStatuses.PartiallyRefunded, purchase.Status);
        Assert.Collection(
            credits.Adjustments,
            opened => Assert.Equal(-80, opened.Delta),
            won => Assert.Equal(80, won.Delta));
    }

    [Fact]
    public async Task RefundBeforeCompletion_IsAppliedAfterThePurchaseGrant()
    {
        await using var context = CreateContext();
        var purchase = PaidPurchase();
        purchase.Status = StripeCreditPurchaseStatuses.Pending;
        purchase.PaidAt = null;
        context.StripeCreditPurchases.Add(purchase);
        await context.SaveChangesAsync();
        var credits = new RecordingCreditService();
        var service = CreateService(context, credits);

        await service.HandleRefundAsync("evt_refund", "re_early", "pi_1", 2_950, "succeeded");
        Assert.Empty(credits.Adjustments);

        await service.HandleCompletedCheckoutAsync(
            "evt_paid",
            "cs_1",
            "paid",
            "pi_1",
            5_900,
            "sek",
            new Dictionary<string, string> { ["purchase_id"] = purchase.Id.ToString("D") });
        await service.HandleCompletedCheckoutAsync(
            "evt_paid_retry",
            "cs_1",
            "paid",
            "pi_1",
            5_900,
            "sek",
            new Dictionary<string, string> { ["purchase_id"] = purchase.Id.ToString("D") });

        await context.Entry(purchase).ReloadAsync();
        Assert.Equal(1, credits.GrantCount);
        Assert.Equal([($"purchase-reconciliation:{purchase.Id:D}", -50)], credits.Adjustments);
        Assert.Equal(50, purchase.RevokedCredits);
        Assert.Equal(StripeCreditPurchaseStatuses.PartiallyRefunded, purchase.Status);
    }

    private static StripeCreditPurchase PaidPurchase() => new()
    {
        Id = Guid.NewGuid(),
        UserId = "user-1",
        PackageKey = "starter",
        PriceId = "price_1",
        UnitAmountMinor = 5_900,
        Currency = "sek",
        Credits = 100,
        DisplayName = "Starter",
        Status = StripeCreditPurchaseStatuses.Paid,
        StripeCheckoutSessionId = "cs_1",
        StripePaymentIntentId = "pi_1",
        PaidAt = DateTimeOffset.UtcNow,
    };

    private static GlosifyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new GlosifyContext(options);
    }

    private static StripePaymentService CreateService(
        GlosifyContext context,
        IAiCreditService credits) =>
        new(
            context,
            credits,
            Options.Create(new StripeOptions
            {
                Enabled = true,
                SecretKey = "rk_test_example",
                WebhookSecret = "whsec_example",
            }),
            NullLogger<StripePaymentService>.Instance);

    private sealed class RecordingCreditService : IAiCreditService
    {
        public List<(string Id, int Delta)> Adjustments { get; } = [];
        public int GrantCount { get; private set; }

        public Task<bool> GrantStripePurchaseAsync(
            string targetUserId,
            string purchaseId,
            int credits,
            string note,
            CancellationToken cancellationToken = default)
        {
            GrantCount++;
            return Task.FromResult(true);
        }

        public Task<bool> ApplyStripePaymentAdjustmentAsync(
            string targetUserId,
            string adjustmentId,
            int creditDelta,
            string note,
            CancellationToken cancellationToken = default)
        {
            Adjustments.Add((adjustmentId, creditDelta));
            return Task.FromResult(true);
        }

        public Task<AiCreditAccountView> GetOrCreateAccountAsync(string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AiCreditTransaction>> GetRecentTransactionsAsync(
            string userId,
            int count = 25,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AiCreditReservation> ReserveAsync(
            AiUsageContext context,
            string provider,
            string model,
            int estimatedTokens,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CommitUsageAsync(
            Guid reservationId,
            AiTokenUsage usage,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task GrantAsync(
            string adminUserId,
            string targetUserId,
            int credits,
            string note,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
