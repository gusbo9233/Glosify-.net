using Glosify.Models.Entities;
using Glosify.Services.Ai;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Auth;

public sealed record DemoAccountSeedResult(ApplicationUser? User, string? Error)
{
    public static DemoAccountSeedResult Seeded(ApplicationUser user) => new(user, null);

    public static DemoAccountSeedResult Failed(string error) => new(null, error);
}

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

    /// <summary>
    /// Returns the demo user, creating and funding it if needed. The failure reason comes
    /// back to the caller rather than only reaching the log, because App Service attaches
    /// Application Insights without the SDK and so captures nothing logged during startup.
    /// </summary>
    public async Task<DemoAccountSeedResult> EnsureAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return DemoAccountSeedResult.Failed("Demo access is turned off (Demo:Enabled is false).");
        }

        if (!_options.IsConfigured)
        {
            var incomplete =
                "Demo access is enabled but incomplete (Demo:Email, Demo:Password, Demo:AccessCode and a positive Demo:Credits are all required).";
            _logger.LogWarning("{Reason} The demo account was not seeded.", incomplete);
            return DemoAccountSeedResult.Failed(incomplete);
        }

        var (user, error) = await EnsureUserAsync(cancellationToken);
        if (user is null)
        {
            return DemoAccountSeedResult.Failed(error ?? "The demo account could not be created.");
        }

        await EnsureCreditsAsync(user.Id, cancellationToken);
        return DemoAccountSeedResult.Seeded(user);
    }

    private async Task<(ApplicationUser? User, string? Error)> EnsureUserAsync(CancellationToken cancellationToken)
    {
        var user = await _userManager.FindByEmailAsync(_options.Email);
        if (user is not null)
        {
            return (user, null);
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
            return (user, null);
        }

        // Two instances starting together race here; the loser sees a duplicate-user
        // error and can just use the row the winner wrote.
        var existing = await _userManager.FindByEmailAsync(_options.Email);
        if (existing is not null)
        {
            return (existing, null);
        }

        var errors = string.Join("; ", result.Errors.Select(error => $"{error.Code}: {error.Description}"));
        _logger.LogError("Could not create the demo account {Email}: {Errors}", _options.Email, errors);
        return (null, $"Could not create the demo account: {errors}");
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
