using System.Security.Claims;
using Glosify.Services.Ai;
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
    private readonly IConfiguration _configuration;
    private readonly ILogger<AiCreditPillViewComponent> _logger;

    public AiCreditPillViewComponent(
        IAiCreditService credits,
        IConfiguration configuration,
        ILogger<AiCreditPillViewComponent> logger)
    {
        _credits = credits;
        _configuration = configuration;
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

            return View(new AiCreditPillModel(account.AvailableCredits, IsAdmin()));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not read the AI credit balance for the top bar");
            return Content(string.Empty);
        }
    }

    private bool IsAdmin()
    {
        var account = UserClaimsPrincipal.FindFirstValue(ClaimTypes.Email)
            ?? UserClaimsPrincipal.Identity?.Name;
        if (string.IsNullOrEmpty(account))
        {
            return false;
        }

        var adminEmails = _configuration.GetSection("Admin:Emails").Get<string[]>() ?? [];
        return adminEmails.Any(email => string.Equals(email, account, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record AiCreditPillModel(int AvailableCredits, bool IsAdmin);
