using Glosify.Extensions;
using Glosify.Models.ViewModels;
using Glosify.Services.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;

namespace Glosify.Controllers;

[Authorize]
public sealed class PaymentsController : Controller
{
    private readonly IStripePaymentService _payments;
    private readonly StripeOptions _stripeOptions;
    private readonly ILogger<PaymentsController> _logger;

    public PaymentsController(
        IStripePaymentService payments,
        IOptions<StripeOptions> stripeOptions,
        ILogger<PaymentsController> logger)
    {
        _payments = payments;
        _stripeOptions = stripeOptions.Value;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Index()
    {
        return View(new PaymentIndexViewModel
        {
            IsEnabled = _payments.IsEnabled,
            Packages = _payments.GetCreditPackages()
                .Select(package => new PaymentPackageViewModel
                {
                    Key = package.Key,
                    DisplayName = package.DisplayName,
                    DisplayPrice = StripePriceFormatter.Format(package.UnitAmountMinor, package.Currency),
                    Credits = package.Credits,
                })
                .ToList(),
        });
    }

    [HttpPost]
    public async Task<IActionResult> CreateCheckoutSession(
        string packageKey,
        CancellationToken cancellationToken)
    {
        if (!_payments.IsEnabled)
        {
            return NotFound();
        }

        try
        {
            var url = await _payments.CreateCheckoutSessionAsync(
                User.GetUserId(),
                User.Identity?.Name,
                packageKey,
                cancellationToken);
            return Redirect(url);
        }
        catch (ArgumentException)
        {
            TempData["PaymentsMessage"] = "That credit package is no longer available.";
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpGet]
    public async Task<IActionResult> Success(
        string? session_id,
        CancellationToken cancellationToken)
    {
        if (!_payments.IsEnabled)
        {
            return NotFound();
        }

        var confirmation = await _payments.ConfirmCheckoutSessionAsync(
            User.GetUserId(),
            session_id ?? string.Empty,
            cancellationToken);
        return View(new PaymentSuccessViewModel
        {
            IsPaid = confirmation.IsPaid,
            WasFulfilled = confirmation.WasFulfilled,
            Message = confirmation.Message,
        });
    }

    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [RequestSizeLimit(262_144)]
    [HttpPost]
    public async Task<IActionResult> Webhook(CancellationToken cancellationToken)
    {
        if (!_payments.IsEnabled)
        {
            return NotFound();
        }

        var signature = Request.Headers["Stripe-Signature"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(signature))
        {
            return BadRequest();
        }

        using var reader = new StreamReader(Request.Body);
        var payload = await reader.ReadToEndAsync(cancellationToken);
        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(
                payload,
                signature,
                _stripeOptions.WebhookSecret);
        }
        catch (StripeException exception)
        {
            _logger.LogWarning(exception, "Rejected Stripe webhook signature.");
            return BadRequest();
        }

        if (stripeEvent.Type is "refund.created" or "refund.updated"
            && stripeEvent.Data.Object is Stripe.Refund refund)
        {
            var handled = await _payments.HandleRefundAsync(
                stripeEvent.Id ?? $"refund:{refund.Id}",
                refund.Id,
                refund.PaymentIntentId,
                refund.Amount,
                refund.Status ?? string.Empty,
                cancellationToken);
            return handled ? Ok() : StatusCode(StatusCodes.Status500InternalServerError);
        }

        if (stripeEvent.Type is "charge.dispute.created" or "charge.dispute.closed"
            && stripeEvent.Data.Object is Dispute dispute)
        {
            var handled = await _payments.HandleDisputeAsync(
                stripeEvent.Id ?? $"dispute:{dispute.Id}:{dispute.Status}",
                dispute.Id,
                dispute.PaymentIntentId,
                stripeEvent.Type,
                dispute.Status ?? string.Empty,
                cancellationToken);
            return handled ? Ok() : StatusCode(StatusCodes.Status500InternalServerError);
        }

        if (stripeEvent.Type is not ("checkout.session.completed" or "checkout.session.async_payment_succeeded"))
        {
            return Ok();
        }

        if (stripeEvent.Data.Object is not Session session)
        {
            _logger.LogWarning("Stripe webhook {EventId} did not contain a Checkout Session.", stripeEvent.Id);
            return BadRequest();
        }

        var confirmation = await _payments.HandleCompletedCheckoutAsync(
            stripeEvent.Id ?? $"webhook:{session.Id}",
            session.Id,
            session.PaymentStatus ?? string.Empty,
            session.PaymentIntentId,
            session.AmountTotal,
            session.Currency,
            session.Metadata,
            cancellationToken);
        if (string.Equals(session.PaymentStatus, "paid", StringComparison.OrdinalIgnoreCase)
            && !confirmation.IsPaid)
        {
            // Preserve the event for retry when Stripe has confirmed payment but
            // Glosify cannot yet match or fulfill the purchase.
            return StatusCode(StatusCodes.Status500InternalServerError);
        }

        return Ok();
    }
}
