using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public sealed class TrialEligibilityServiceTests
{
    [Theory]
    [InlineData("Google", true)]
    [InlineData("Microsoft", true)]
    [InlineData("GitHub", false)]
    public async Task OnlySupportedIdentityExternalLoginsQualify(string provider, bool expected)
    {
        await using var context = new GlosifyContext(
            new DbContextOptionsBuilder<GlosifyContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
        context.Users.Add(new ApplicationUser { Id = "user-1", UserName = "user@example.test" });
        context.UserLogins.Add(new IdentityUserLogin<string>
        {
            UserId = "user-1",
            LoginProvider = provider,
            ProviderKey = "provider-key",
            ProviderDisplayName = provider,
        });
        await context.SaveChangesAsync();

        Assert.Equal(expected, await new TrialEligibilityService(context).IsEligibleAsync("user-1"));
        Assert.False(await new TrialEligibilityService(context).IsEligibleAsync("password-only"));
    }
}
