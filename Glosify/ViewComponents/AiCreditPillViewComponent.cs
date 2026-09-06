using Glosify.Extensions;
using Glosify.Services.Ai;
using Glosify.Services.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.ViewComponents;

/// <summary>
/// The credit balance in the top bar, and the admin links beside it.
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
    private readonly AdministratorAccess _administratorAccess;
    private readonly ILogger<AiCreditPillViewComponent> _logger;

    public AiCreditPillViewComponent(
        IAiCreditService credits,
        AdministratorAccess administratorAccess,
        ILogger<AiCreditPillViewComponent> logger)
    {
        _credits = credits;
        _administratorAccess = administratorAccess;
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

            return View(new AiCreditPillModel(account.AvailableCredits, _administratorAccess.IsAdmin(UserClaimsPrincipal)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not read the AI credit balance for the top bar");
            return Content(string.Empty);
        }
    }
}

public sealed record AiCreditPillModel(int AvailableCredits, bool IsAdmin);
