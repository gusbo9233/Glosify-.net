namespace Glosify.Models.Entities;

public sealed class StripePaymentEvent
{
    public string IdempotencyKey { get; set; } = string.Empty;
    public string StripeEventId { get; set; } = string.Empty;
    public Guid PurchaseId { get; set; }
    public string Type { get; set; } = string.Empty;
    public int CreditDelta { get; set; }
    public DateTimeOffset ProcessedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AppliedAt { get; set; }
}
