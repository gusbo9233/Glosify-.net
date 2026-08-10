using Glosify.Data;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Auth;

public interface ITrialEligibilityService
{
    Task<bool> IsEligibleAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// A trial is available only after the Identity account has been linked to one of
/// Glosify's verified consumer OAuth providers. Password and API registrations stay
/// eligible for a later link because this service does not mutate credit state.
/// </summary>
public sealed class TrialEligibilityService(GlosifyContext context) : ITrialEligibilityService
{
    private static readonly string[] EligibleProviders = ["Google", "Microsoft"];

    public Task<bool> IsEligibleAsync(string userId, CancellationToken cancellationToken = default) =>
        context.UserLogins
            .AsNoTracking()
            .AnyAsync(
                login => login.UserId == userId
                    && EligibleProviders.Contains(login.LoginProvider),
                cancellationToken);
}
