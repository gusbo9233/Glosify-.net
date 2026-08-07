using Glosify.Services.Ai;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Auth;

/// <summary>
/// Keeps the shared demo account present and funded. Runs at startup so a fresh
/// deployment (or a demo account someone spent down) is usable without a manual
/// admin grant.
/// </summary>
public sealed class DemoAccountSeeder
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAiCreditService _credits;
    private readonly DemoAccountOptions _options;
    private readonly ILogger<DemoAccountSeeder> _logger;

    public DemoAccountSeeder(
        UserManager<ApplicationUser> userManager,
        IAiCreditService credits,
        IOptions<DemoAccountOptions> options,
        ILogger<DemoAccountSeeder> logger)
    {
        _userManager = userManager;
        _credits = credits;
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return;
        }

        if (!_options.IsConfigured)
        {
            _logger.LogWarning(
                "Demo access is enabled but incomplete (Demo:Email, Demo:Password, Demo:AccessCode and a positive Demo:Credits are all required). The demo account was not seeded.");
            return;
        }

        var user = await EnsureUserAsync(cancellationToken);
        if (user is null)
        {
            return;
        }

        await EnsureCreditsAsync(user.Id, cancellationToken);
    }

    private async Task<ApplicationUser?> EnsureUserAsync(CancellationToken cancellationToken)
    {
        var user = await _userManager.FindByEmailAsync(_options.Email);
        if (user is not null)
        {
            return user;
        }

        user = new ApplicationUser
        {
            UserName = _options.Email,
            Email = _options.Email,
            // Nobody can read mail for this address, so a confirmation flow would lock
            // the account out if Identity:RequireConfirmedAccount is ever turned on.
            EmailConfirmed = true,
        };

        var result = await _userManager.CreateAsync(user, _options.Password!);
        if (result.Succeeded)
        {
            _logger.LogInformation("Created the demo account {Email}.", _options.Email);
            return user;
        }

        // Two instances starting together race here; the loser sees a duplicate-user
        // error and can just use the row the winner wrote.
        var existing = await _userManager.FindByEmailAsync(_options.Email);
        if (existing is not null)
        {
            return existing;
        }

        _logger.LogError(
            "Could not create the demo account {Email}: {Errors}",
            _options.Email,
            string.Join("; ", result.Errors.Select(error => $"{error.Code}: {error.Description}")));
        return null;
    }

    private async Task EnsureCreditsAsync(string userId, CancellationToken cancellationToken)
    {
        var account = await _credits.GetOrCreateAccountAsync(userId, cancellationToken);
        var shortfall = _options.Credits - account.AvailableCredits;
        if (shortfall <= 0)
        {
            return;
        }

        await _credits.GrantAsync(
            adminUserId: userId,
            targetUserId: userId,
            credits: shortfall,
            note: "Demo account top-up",
            cancellationToken);
        _logger.LogInformation(
            "Topped the demo account up by {Credits} credits to {Target}.",
            shortfall,
            _options.Credits);
    }
}
