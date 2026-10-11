using Glosify.Extensions;
using Glosify.Services.Ai;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.ViewComponents;

/// <summary>
/// The read-only credit balance shown in account settings.
/// </summary>
/// <remarks>
/// <see cref="IAiCreditService.GetOrCreateAccountAsync"/> can write, so this ran a
/// potentially write-committing call from the layout on every authenticated page render.
/// Pulling it into a view component gives it an error path: a balance that cannot be read
/// renders as nothing rather than failing the page.
/// </remarks>
public sealed class AiCreditPillViewComponent : ViewComponent
{
    private readonly IAiCreditService _credits;
    private readonly ILogger<AiCreditPillViewComponent> _logger;

    public AiCreditPillViewComponent(
        IAiCreditService credits,
        ILogger<AiCreditPillViewComponent> logger)
    {
        _credits = credits;
        _logger = logger;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (UserClaimsPrincipal.Identity?.IsAuthenticated != true)
        {
            return Content(string.Empty);
        }

        try
        {
            var account = await _credits.GetOrCreateAccountAsync(
                UserClaimsPrincipal.GetUserId(),
                HttpContext.RequestAborted);

            return View(new AiCreditPillModel(Glosify.Services.Ai.CreditAmounts.Display(account.AvailableCredits)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not read the AI credit balance for account settings");
            return Content(string.Empty);
        }
    }
}

public sealed record AiCreditPillModel(int AvailableCredits);
