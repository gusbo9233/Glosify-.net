using Glosify.Localization;
using Glosify.Models.Entities;
using Glosify.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Glosify.Controllers;

[AllowAnonymous]
public sealed class DisplayLanguageController(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    IOptions<DemoAccountOptions> demoOptions,
    ILogger<DisplayLanguageController> logger) : Controller
{
    [HttpPost("/display-language")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Set(
        string? culture,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        if (!DisplayCultureCatalog.TryCanonicalize(culture, out var canonicalCulture))
        {
            return BadRequest();
        }

        if (User.Identity?.IsAuthenticated == true)
        {
            var user = await userManager.GetUserAsync(User);
            if (user is not null && !IsSharedDemoAccount(user))
            {
                user.DisplayCulture = canonicalCulture;
                var result = await userManager.UpdateAsync(user);
                if (!result.Succeeded)
                {
                    logger.LogError(
                        "Could not save display culture {Culture} for user {UserId}: {Errors}",
                        canonicalCulture,
                        user.Id,
                        string.Join(", ", result.Errors.Select(error => error.Code)));
                    return StatusCode(StatusCodes.Status500InternalServerError);
                }

                await signInManager.RefreshSignInAsync(user);
            }
        }

        DisplayCultureCookie.Append(Response, Request, canonicalCulture);
        DisplayLanguageTelemetry.Record(canonicalCulture, "selector");

        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }

    private bool IsSharedDemoAccount(ApplicationUser user)
    {
        var options = demoOptions.Value;
        return options.Enabled
            && !string.IsNullOrWhiteSpace(options.Email)
            && string.Equals(user.Email, options.Email, StringComparison.OrdinalIgnoreCase);
    }
}
