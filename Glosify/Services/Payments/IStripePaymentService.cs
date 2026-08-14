namespace Glosify.Services.Payments;

public interface IStripePaymentService
{
    bool IsEnabled { get; }

    IReadOnlyList<StripeCreditPackageOptions> GetCreditPackages();

    Task<string> CreateCheckoutSessionAsync(
        string userId,
        string? customerEmail,
        string packageKey,
        CancellationToken cancellationToken = default);

    Task<StripePaymentConfirmation> ConfirmCheckoutSessionAsync(
        string userId,
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<StripePaymentConfirmation> HandleCompletedCheckoutAsync(
        string eventId,
        string sessionId,
        string paymentStatus,
        string? paymentIntentId,
        long? amountTotal,
        string? currency,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken = default);

    Task<bool> HandleRefundAsync(
        string eventId,
        string refundId,
        string? paymentIntentId,
        long amountMinor,
        string status,
        CancellationToken cancellationToken = default);

    Task<bool> HandleDisputeAsync(
        string eventId,
        string disputeId,
        string? paymentIntentId,
        string eventType,
        string status,
        CancellationToken cancellationToken = default);
}

public sealed record StripePaymentConfirmation(
    bool IsPaid,
    bool WasFulfilled,
    string Message);
