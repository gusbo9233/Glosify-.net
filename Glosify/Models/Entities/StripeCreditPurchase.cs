namespace Glosify.Models.Entities;

public sealed class StripeCreditPurchase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public string PackageKey { get; set; } = string.Empty;
    public string PriceId { get; set; } = string.Empty;
    public long UnitAmountMinor { get; set; }
    public string Currency { get; set; } = string.Empty;
    public int Credits { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Status { get; set; } = StripeCreditPurchaseStatuses.Pending;
    public string? StripeCheckoutSessionId { get; set; }
    public string? StripePaymentIntentId { get; set; }
    public string? LastWebhookEventId { get; set; }
    public long RefundedAmountMinor { get; set; }
    public int RevokedCredits { get; set; }
    public bool HasUnresolvedDispute { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PaidAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public static class StripeCreditPurchaseStatuses
{
    public const string Pending = "pending";
    public const string Paid = "paid";
    public const string PartiallyRefunded = "partially_refunded";
    public const string Refunded = "refunded";
    public const string Disputed = "disputed";
}
