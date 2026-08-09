using Glosify.Models.Entities;
using Glosify.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Glosify.Controllers;

/// <summary>
/// One-click entry to the shared demo account: /demo?code=... signs the visitor in
/// without a password. The code is deliberately not shown anywhere in the app, so the
/// link only works for someone who was handed it.
/// </summary>
[AllowAnonymous]
public sealed class DemoController : Controller
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly DemoAccountSeeder _seeder;
    private readonly DemoAccountOptions _options;
    private readonly ILogger<DemoController> _logger;

    public DemoController(
        SignInManager<ApplicationUser> signInManager,
        DemoAccountSeeder seeder,
        IOptions<DemoAccountOptions> options,
        ILogger<DemoController> logger)
    {
        _signInManager = signInManager;
        _seeder = seeder;
        _options = options.Value;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string? code, string? returnUrl = null)
    {
        // 404 rather than 401/403 throughout: a crawler that finds the URL learns
        // nothing about whether a demo exists or whether its code was close.
        if (!_options.IsConfigured)
        {
            return NotFound();
        }

        if (!_options.MatchesAccessCode(code))
        {
            _logger.LogWarning(
                "Rejected a demo sign-in with a missing or wrong access code from {RemoteIp}.",
                HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            return NotFound();
        }

        // Seeding here rather than trusting startup: the account is created on the first
        // visit if it is missing, and any Identity failure is logged from inside a
        // request, which is the only place Application Insights sees it.
        var seed = await _seeder.EnsureAsync(HttpContext.RequestAborted);
        if (seed.User is null)
        {
            _logger.LogError(
                "The demo access code was correct but the demo account {Email} is unavailable: {Error}",
                _options.Email,
                seed.Error);
            return NotFound();
        }

        var user = seed.User;

        // Replaces whatever cookie the visitor already had, so a signed-in user
        // following the link lands in the demo rather than silently staying themselves.
        await _signInManager.SignInAsync(user, isPersistent: false);
        _logger.LogInformation(
            "Signed a visitor in to the demo account from {RemoteIp}.",
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");

        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }
}
