using System.Security.Claims;
using Glosify.Models.Entities;
using Microsoft.AspNetCore.Identity;

namespace Glosify.Services.Auth;

public sealed record ExternalAccountResolution(ApplicationUser? User, string? ErrorMessage)
{
    public bool Succeeded => User is not null;
}

public interface IExternalAccountService
{
    Task<ExternalAccountResolution> ResolveOrCreateAsync(ExternalLoginInfo info);
}

public interface IExternalAccountUserStore
{
    Task<ApplicationUser?> FindByLoginAsync(string provider, string providerKey);
    Task<ApplicationUser?> FindByEmailAsync(string email);
    Task<IdentityResult> CreateAsync(ApplicationUser user);
    Task<IdentityResult> AddLoginAsync(ApplicationUser user, ExternalLoginInfo info);
    Task<IdentityResult> DeleteAsync(ApplicationUser user);
}

public sealed class IdentityExternalAccountUserStore(
    UserManager<ApplicationUser> userManager) : IExternalAccountUserStore
{
    public Task<ApplicationUser?> FindByLoginAsync(string provider, string providerKey) =>
        userManager.FindByLoginAsync(provider, providerKey);

    public Task<ApplicationUser?> FindByEmailAsync(string email) =>
        userManager.FindByEmailAsync(email);

    public Task<IdentityResult> CreateAsync(ApplicationUser user) =>
        userManager.CreateAsync(user);

    public Task<IdentityResult> AddLoginAsync(ApplicationUser user, ExternalLoginInfo info) =>
        userManager.AddLoginAsync(user, info);

    public Task<IdentityResult> DeleteAsync(ApplicationUser user) =>
        userManager.DeleteAsync(user);
}

public sealed class ExternalAccountService(
    IExternalAccountUserStore userStore,
    ILogger<ExternalAccountService> logger) : IExternalAccountService
{
    public async Task<ExternalAccountResolution> ResolveOrCreateAsync(ExternalLoginInfo info)
    {
        var user = await userStore.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
        if (user is not null)
        {
            return new ExternalAccountResolution(user, null);
        }

        var email = GetVerifiedEmailClaim(info);
        if (string.IsNullOrWhiteSpace(email))
        {
            return Failure($"{info.ProviderDisplayName ?? info.LoginProvider} did not provide an email address.");
        }

        user = await userStore.FindByEmailAsync(email);
        if (user is not null)
        {
            if (!string.Equals(info.LoginProvider, "Google", StringComparison.OrdinalIgnoreCase))
            {
                return Failure("An account with this email already exists. Sign in with the method you originally used.");
            }

            var linkResult = await userStore.AddLoginAsync(user, info);
            return linkResult.Succeeded
                ? new ExternalAccountResolution(user, null)
                : Failure("Could not link the external account.", linkResult);
        }

        user = new ApplicationUser { UserName = email, Email = email };
        var createResult = await userStore.CreateAsync(user);
        if (!createResult.Succeeded)
        {
            return Failure("Could not create an account.", createResult);
        }

        var addLoginResult = await userStore.AddLoginAsync(user, info);
        if (addLoginResult.Succeeded)
        {
            return new ExternalAccountResolution(user, null);
        }

        logger.LogWarning(
            "External login association failed after creating user {UserId}: {Errors}",
            user.Id,
            Describe(addLoginResult));
        var deleteResult = await userStore.DeleteAsync(user);
        if (!deleteResult.Succeeded)
        {
            logger.LogError(
                "Could not compensate external-login user creation for {UserId}: {Errors}",
                user.Id,
                Describe(deleteResult));
        }

        return Failure("Could not link the external account.");
    }

    private ExternalAccountResolution Failure(string publicMessage, IdentityResult? result = null)
    {
        if (result is not null)
        {
            logger.LogWarning("External account resolution failed: {Errors}", Describe(result));
        }

        return new ExternalAccountResolution(null, publicMessage);
    }

    private static string Describe(IdentityResult result) =>
        string.Join("; ", result.Errors.Select(error => $"{error.Code}: {error.Description}"));

    private static string GetVerifiedEmailClaim(ExternalLoginInfo info) =>
        info.Principal.FindFirst(ClaimTypes.Email)?.Value
        ?? info.Principal.FindFirst("email")?.Value
        ?? string.Empty;
}
