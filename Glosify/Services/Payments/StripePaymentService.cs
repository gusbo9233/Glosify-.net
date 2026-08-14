using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;
using System.Security.Cryptography;
using System.Text;

namespace Glosify.Services.Payments;

public sealed class StripePaymentService : IStripePaymentService
{
    private const string PurchaseMetadataKey = "purchase_id";
    private const string IntegrationIdentifierPrefix = "glosify_credits_";

    private readonly GlosifyContext _context;
    private readonly IAiCreditService _credits;
    private readonly StripeOptions _options;
    private readonly StripeClient? _client;
    private readonly ILogger<StripePaymentService> _logger;

    public StripePaymentService(
        GlosifyContext context,
        IAiCreditService credits,
        IOptions<StripeOptions> options,
        ILogger<StripePaymentService> logger)
    {
        _context = context;
        _credits = credits;
        _options = options.Value;
        _client = _options.Enabled ? new StripeClient(_options.SecretKey) : null;
        _logger = logger;
    }

    public bool IsEnabled => _options.Enabled;

    public IReadOnlyList<StripeCreditPackageOptions> GetCreditPackages() =>
        _options.CreditPackages;

    public async Task<string> CreateCheckoutSessionAsync(
        string userId,
        string? customerEmail,
        string packageKey,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        var package = FindPackage(packageKey);
        var price = await Client.V1.Prices.GetAsync(
            package.PriceId,
            cancellationToken: cancellationToken);
        if (!PriceMatchesPackage(price, package))
        {
            throw new InvalidOperationException(
                $"Stripe Price '{package.PriceId}' does not match the configured amount, currency, and one-time payment type.");
        }

        var purchase = new StripeCreditPurchase
        {
            UserId = userId,
            PackageKey = package.Key,
            PriceId = package.PriceId,
            UnitAmountMinor = package.UnitAmountMinor,
            Currency = package.Currency.ToLowerInvariant(),
            Credits = package.Credits,
            DisplayName = package.DisplayName,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _context.StripeCreditPurchases.Add(purchase);
        await _context.SaveChangesAsync(cancellationToken);

        var sessionOptions = new SessionCreateOptions
        {
            Mode = "payment",
            LineItems =
            [
                new SessionLineItemOptions
                {
                    Price = package.PriceId,
                    Quantity = 1,
                },
            ],
            SuccessUrl = BuildUrl($"/Payments/Success?session_id={{CHECKOUT_SESSION_ID}}"),
            CancelUrl = BuildUrl("/Payments"),
            ClientReferenceId = purchase.Id.ToString("D"),
            CustomerEmail = string.IsNullOrWhiteSpace(customerEmail) ? null : customerEmail,
            Metadata = new Dictionary<string, string>
            {
                [PurchaseMetadataKey] = purchase.Id.ToString("D"),
            },
            PaymentIntentData = new SessionPaymentIntentDataOptions
            {
                Metadata = new Dictionary<string, string>
                {
                    [PurchaseMetadataKey] = purchase.Id.ToString("D"),
                },
            },
            IntegrationIdentifier = BuildIntegrationIdentifier(),
        };

        var session = await Client.V1.Checkout.Sessions.CreateAsync(
            sessionOptions,
            cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(session.Url))
        {
            throw new InvalidOperationException("Stripe did not return a Checkout URL.");
        }

        purchase.StripeCheckoutSessionId = session.Id;
        await _context.SaveChangesAsync(cancellationToken);
        return session.Url;
    }

    public async Task<StripePaymentConfirmation> ConfirmCheckoutSessionAsync(
        string userId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new StripePaymentConfirmation(false, false, "The payment session was not provided.");
        }

        var session = await Client.V1.Checkout.Sessions.GetAsync(
            sessionId,
            cancellationToken: cancellationToken);
        var purchaseId = GetPurchaseId(session.Metadata);
        if (purchaseId is null)
        {
            return new StripePaymentConfirmation(false, false, "The payment could not be matched to a Glosify purchase.");
        }

        var belongsToUser = await _context.StripeCreditPurchases
            .AsNoTracking()
            .AnyAsync(purchase => purchase.Id == purchaseId && purchase.UserId == userId, cancellationToken);
        if (!belongsToUser)
        {
            return new StripePaymentConfirmation(false, false, "The payment could not be matched to your account.");
        }

        return await HandleCompletedCheckoutAsync(
            $"success-page:{session.Id}",
            session.Id,
            session.PaymentStatus ?? string.Empty,
            session.PaymentIntentId,
            session.AmountTotal,
            session.Currency,
            session.Metadata,
            cancellationToken);
    }

    public async Task<StripePaymentConfirmation> HandleCompletedCheckoutAsync(
        string eventId,
        string sessionId,
        string paymentStatus,
        string? paymentIntentId,
        long? amountTotal,
        string? currency,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        if (!string.Equals(paymentStatus, "paid", StringComparison.OrdinalIgnoreCase))
        {
            return new StripePaymentConfirmation(false, false, "Payment is still awaiting confirmation.");
        }

        var purchaseId = GetPurchaseId(metadata);
        if (purchaseId is null)
        {
            _logger.LogWarning("Ignoring Stripe session {SessionId} without a valid purchase id.", sessionId);
            return new StripePaymentConfirmation(false, false, "The payment could not be matched to a Glosify purchase.");
        }

        var purchase = await _context.StripeCreditPurchases
            .SingleOrDefaultAsync(item => item.Id == purchaseId, cancellationToken);
        if (purchase is null)
        {
            _logger.LogWarning("Ignoring Stripe session {SessionId} for unknown purchase {PurchaseId}.", sessionId, purchaseId);
            return new StripePaymentConfirmation(false, false, "The payment could not be matched to a Glosify purchase.");
        }

        if (purchase.StripeCheckoutSessionId is not null
            && !string.Equals(purchase.StripeCheckoutSessionId, sessionId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Ignoring mismatched Stripe session {SessionId} for purchase {PurchaseId}.",
                sessionId,
                purchaseId);
            return new StripePaymentConfirmation(false, false, "The payment could not be matched to a Glosify purchase.");
        }

        if (amountTotal != purchase.UnitAmountMinor
            || !string.Equals(currency, purchase.Currency, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                "Refusing to fulfill Stripe session {SessionId}: expected {ExpectedAmount} {ExpectedCurrency}, received {ActualAmount} {ActualCurrency}.",
                sessionId,
                purchase.UnitAmountMinor,
                purchase.Currency,
                amountTotal,
                currency);
            return new StripePaymentConfirmation(false, false, "The payment amount could not be verified.");
        }

        if (purchase.PaidAt is not null)
        {
            return new StripePaymentConfirmation(true, false, "Payment confirmed. Your credits are ready.");
        }

        var wasGranted = await _credits.GrantStripePurchaseAsync(
            purchase.UserId,
            purchase.Id.ToString("D"),
            purchase.Credits,
            $"Stripe purchase {sessionId}: {purchase.DisplayName}.",
            cancellationToken);

        if (purchase.RevokedCredits > 0)
        {
            await _credits.ApplyStripePaymentAdjustmentAsync(
                purchase.UserId,
                $"purchase-reconciliation:{purchase.Id:D}",
                -purchase.RevokedCredits,
                $"Stripe refund or dispute already recorded for purchase {purchase.Id:D}.",
                cancellationToken);
        }

        purchase.StripeCheckoutSessionId ??= sessionId;
        purchase.StripePaymentIntentId = paymentIntentId;
        purchase.LastWebhookEventId = eventId;
        purchase.PaidAt = DateTimeOffset.UtcNow;
        purchase.Status = PurchaseStatus(purchase);
        await _context.SaveChangesAsync(cancellationToken);

        return new StripePaymentConfirmation(
            true,
            wasGranted,
            "Payment confirmed. Your credits are ready.");
    }

    public Task<bool> HandleRefundAsync(
        string eventId,
        string refundId,
        string? paymentIntentId,
        long amountMinor,
        string status,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(true);
        }

        return ReconcileAdjustmentAsync(
            BuildAdjustmentKey("refund", refundId),
            eventId,
            "refund",
            paymentIntentId,
            (purchase, _) =>
            {
                purchase.RefundedAmountMinor = Math.Min(
                    purchase.UnitAmountMinor,
                    checked(purchase.RefundedAmountMinor + amountMinor));
                return TargetRevokedCredits(purchase);
            },
            cancellationToken);
    }

    public Task<bool> HandleDisputeAsync(
        string eventId,
        string disputeId,
        string? paymentIntentId,
        string eventType,
        string status,
        CancellationToken cancellationToken = default)
    {
        var isClosed = string.Equals(eventType, "charge.dispute.closed", StringComparison.Ordinal);
        var releasesHold = isClosed
            && (string.Equals(status, "won", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "warning_closed", StringComparison.OrdinalIgnoreCase));

        return ReconcileAdjustmentAsync(
            BuildAdjustmentKey("dispute", disputeId, status),
            eventId,
            eventType,
            paymentIntentId,
            (purchase, _) =>
            {
                purchase.HasUnresolvedDispute = !releasesHold;
                return TargetRevokedCredits(purchase);
            },
            cancellationToken);
    }

    private async Task<bool> ReconcileAdjustmentAsync(
        string idempotencyKey,
        string eventId,
        string eventType,
        string? paymentIntentId,
        Func<StripeCreditPurchase, StripePaymentEvent, int> updatePurchase,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var paymentEvent = await _context.StripePaymentEvents
            .SingleOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, cancellationToken);
        if (paymentEvent?.AppliedAt is not null)
        {
            return true;
        }

        StripeCreditPurchase? purchase;
        if (paymentEvent is null)
        {
            if (string.IsNullOrWhiteSpace(paymentIntentId))
            {
                _logger.LogWarning("Ignoring Stripe {EventType} event {EventId} without a PaymentIntent.", eventType, eventId);
                return true;
            }

            purchase = await _context.StripeCreditPurchases.SingleOrDefaultAsync(
                item => item.StripePaymentIntentId == paymentIntentId,
                cancellationToken);
            if (purchase is null)
            {
                var paymentIntent = await Client.V1.PaymentIntents.GetAsync(
                    paymentIntentId,
                    cancellationToken: cancellationToken);
                var purchaseId = GetPurchaseId(paymentIntent.Metadata);
                if (purchaseId is null)
                {
                    _logger.LogInformation(
                        "Ignoring Stripe {EventType} event {EventId} for an unrelated PaymentIntent {PaymentIntentId}.",
                        eventType,
                        eventId,
                        paymentIntentId);
                    return true;
                }

                purchase = await _context.StripeCreditPurchases.SingleOrDefaultAsync(
                    item => item.Id == purchaseId,
                    cancellationToken);
                if (purchase is null)
                {
                    _logger.LogWarning(
                        "Stripe {EventType} event {EventId} references missing purchase {PurchaseId}.",
                        eventType,
                        eventId,
                        purchaseId);
                    return false;
                }

                purchase.StripePaymentIntentId ??= paymentIntentId;
            }

            paymentEvent = new StripePaymentEvent
            {
                IdempotencyKey = idempotencyKey,
                StripeEventId = eventId,
                PurchaseId = purchase.Id,
                Type = eventType,
                ProcessedAt = DateTimeOffset.UtcNow,
            };
            var targetRevokedCredits = updatePurchase(purchase, paymentEvent);
            paymentEvent.CreditDelta = purchase.PaidAt is null
                ? 0
                : purchase.RevokedCredits - targetRevokedCredits;
            purchase.RevokedCredits = targetRevokedCredits;
            purchase.LastWebhookEventId = eventId;
            purchase.Status = PurchaseStatus(purchase);
            _context.StripePaymentEvents.Add(paymentEvent);
            await _context.SaveChangesAsync(cancellationToken);
        }
        else
        {
            purchase = await _context.StripeCreditPurchases.SingleAsync(
                item => item.Id == paymentEvent.PurchaseId,
                cancellationToken);
        }

        if (paymentEvent.CreditDelta != 0)
        {
            await _credits.ApplyStripePaymentAdjustmentAsync(
                purchase.UserId,
                paymentEvent.IdempotencyKey,
                paymentEvent.CreditDelta,
                $"Stripe {eventType} for purchase {purchase.Id:D}.",
                cancellationToken);
        }

        paymentEvent.AppliedAt = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    internal static int CalculateRefundedCredits(int credits, long refundedAmountMinor, long totalAmountMinor)
    {
        if (credits <= 0 || refundedAmountMinor <= 0 || totalAmountMinor <= 0)
        {
            return 0;
        }

        var numerator = checked((long)credits * Math.Min(refundedAmountMinor, totalAmountMinor));
        return checked((int)Math.Min(credits, (numerator + totalAmountMinor - 1) / totalAmountMinor));
    }

    internal static bool PriceMatchesPackage(Price price, StripeCreditPackageOptions package) =>
        price.Active
        && string.Equals(price.Type, "one_time", StringComparison.Ordinal)
        && price.UnitAmount == package.UnitAmountMinor
        && string.Equals(price.Currency, package.Currency, StringComparison.OrdinalIgnoreCase);

    private static int TargetRevokedCredits(StripeCreditPurchase purchase) =>
        purchase.HasUnresolvedDispute
            ? purchase.Credits
            : CalculateRefundedCredits(
                purchase.Credits,
                purchase.RefundedAmountMinor,
                purchase.UnitAmountMinor);

    private static string PurchaseStatus(StripeCreditPurchase purchase)
    {
        if (purchase.PaidAt is null)
        {
            return StripeCreditPurchaseStatuses.Pending;
        }
        if (purchase.HasUnresolvedDispute)
        {
            return StripeCreditPurchaseStatuses.Disputed;
        }
        if (purchase.RefundedAmountMinor >= purchase.UnitAmountMinor)
        {
            return StripeCreditPurchaseStatuses.Refunded;
        }
        if (purchase.RefundedAmountMinor > 0)
        {
            return StripeCreditPurchaseStatuses.PartiallyRefunded;
        }
        return StripeCreditPurchaseStatuses.Paid;
    }

    private StripeCreditPackageOptions FindPackage(string packageKey)
    {
        var package = _options.CreditPackages.FirstOrDefault(item =>
            string.Equals(item.Key, packageKey, StringComparison.OrdinalIgnoreCase));
        return package ?? throw new ArgumentException("The selected credit package is not available.", nameof(packageKey));
    }

    private string BuildUrl(string path) =>
        $"{_options.PublicBaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

    private static string BuildIntegrationIdentifier()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz";
        var bytes = RandomNumberGenerator.GetBytes(8);
        return IntegrationIdentifierPrefix + new string(bytes.Select(value => alphabet[value % alphabet.Length]).ToArray());
    }

    private static string BuildAdjustmentKey(params string[] parts)
    {
        var value = string.Join('\n', parts);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"stripe:{Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    private static Guid? GetPurchaseId(IReadOnlyDictionary<string, string>? metadata)
    {
        return metadata is not null
            && metadata.TryGetValue(PurchaseMetadataKey, out var value)
            && Guid.TryParse(value, out var purchaseId)
            ? purchaseId
            : null;
    }

    private StripeClient Client =>
        _client ?? throw new InvalidOperationException("Stripe payments are not configured.");

    private void EnsureEnabled()
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("Stripe payments are not configured.");
        }
    }
}
