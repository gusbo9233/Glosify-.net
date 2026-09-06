using System.Security.Claims;
using Glosify.Services.Auth;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Glosify.Tests;

public sealed class AdministratorAccessTests
{
    [Theory]
    [InlineData(null, "admin-1", true, false)]
    [InlineData("", "admin-1", true, false)]
    [InlineData(" ", " ", true, false)]
    [InlineData("admin-1", "admin-1", true, true)]
    [InlineData("admin-1", "ADMIN-1", true, false)]
    [InlineData("admin-1", "admin-1", false, false)]
    [InlineData("admin-1", null, true, false)]
    public void GrantRequiresAuthenticatedExactNonblankId(string? configuredId, string? userId, bool authenticated, bool expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Admin:UserIds:0"] = configuredId,
            ["Admin:Emails:0"] = "admin@example.test",
        }).Build();
        var claims = new List<Claim> { new(ClaimTypes.Email, "admin@example.test"), new(ClaimTypes.Name, "admin@example.test") };
        if (userId is not null) claims.Add(new(ClaimTypes.NameIdentifier, userId));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticated ? "test" : null));

        Assert.Equal(expected, new AdministratorAccess(configuration).IsAdmin(principal));
    }
}
