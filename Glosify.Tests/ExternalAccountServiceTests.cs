using System.Security.Claims;
using Glosify.Models.Entities;
using Glosify.Services.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Glosify.Tests;

public sealed class ExternalAccountServiceTests
{
    [Fact]
    public async Task FailedLoginAssociation_DeletesNewlyCreatedUser()
    {
        var store = new StubExternalAccountUserStore
        {
            AddLoginResult = IdentityResult.Failed(new IdentityError
            {
                Code = "LoginAlreadyAssociated",
                Description = "Login already associated.",
            }),
        };
        var service = CreateService(store);

        var result = await service.ResolveOrCreateAsync(CreateLogin("Google"));

        Assert.False(result.Succeeded);
        Assert.NotNull(store.CreatedUser);
        Assert.Same(store.CreatedUser, store.DeletedUser);
    }

    [Fact]
    public async Task SuccessfulLoginAssociation_ReturnsCreatedUser()
    {
        var store = new StubExternalAccountUserStore();
        var service = CreateService(store);

        var result = await service.ResolveOrCreateAsync(CreateLogin("Google"));

        Assert.True(result.Succeeded);
        Assert.Same(store.CreatedUser, result.User);
        Assert.Null(store.DeletedUser);
    }

    [Fact]
    public async Task NonGoogleProvider_DoesNotLinkByEmail()
    {
        var existing = new ApplicationUser { Id = "existing", Email = "learner@example.test" };
        var store = new StubExternalAccountUserStore { ExistingByEmail = existing };
        var service = CreateService(store);

        var result = await service.ResolveOrCreateAsync(CreateLogin("Microsoft"));

        Assert.False(result.Succeeded);
        Assert.Equal(0, store.AddLoginCalls);
        Assert.Null(store.CreatedUser);
    }

    private static ExternalAccountService CreateService(IExternalAccountUserStore store) =>
        new(store, NullLogger<ExternalAccountService>.Instance);

    private static ExternalLoginInfo CreateLogin(string provider)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Email, "learner@example.test")],
            provider);
        return new ExternalLoginInfo(
            new ClaimsPrincipal(identity),
            provider,
            "provider-key",
            provider);
    }

    private sealed class StubExternalAccountUserStore : IExternalAccountUserStore
    {
        public ApplicationUser? ExistingByLogin { get; set; }
        public ApplicationUser? ExistingByEmail { get; set; }
        public IdentityResult CreateResult { get; set; } = IdentityResult.Success;
        public IdentityResult AddLoginResult { get; set; } = IdentityResult.Success;
        public IdentityResult DeleteResult { get; set; } = IdentityResult.Success;
        public ApplicationUser? CreatedUser { get; private set; }
        public ApplicationUser? DeletedUser { get; private set; }
        public int AddLoginCalls { get; private set; }

        public Task<ApplicationUser?> FindByLoginAsync(string provider, string providerKey) =>
            Task.FromResult(ExistingByLogin);

        public Task<ApplicationUser?> FindByEmailAsync(string email) =>
            Task.FromResult(ExistingByEmail);

        public Task<IdentityResult> CreateAsync(ApplicationUser user)
        {
            CreatedUser = user;
            return Task.FromResult(CreateResult);
        }

        public Task<IdentityResult> AddLoginAsync(ApplicationUser user, ExternalLoginInfo info)
        {
            AddLoginCalls++;
            return Task.FromResult(AddLoginResult);
        }

        public Task<IdentityResult> DeleteAsync(ApplicationUser user)
        {
            DeletedUser = user;
            return Task.FromResult(DeleteResult);
        }
    }
}
