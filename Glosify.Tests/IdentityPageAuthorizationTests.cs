using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Glosify.Tests;

/// <summary>
/// Identity UI protects its account-management pages with
/// <c>AuthorizeAreaFolder</c>/<c>AuthorizeAreaPage</c> conventions rather than
/// <c>[Authorize]</c> attributes. Mapping Razor Pages with a blanket
/// <c>AllowAnonymous()</c> adds <c>IAllowAnonymous</c> metadata, which short-circuits the
/// authorization middleware and silently drops those conventions. These tests pin both
/// halves: the managed pages stay closed, and sign-in stays open.
/// </summary>
public sealed class IdentityPageAuthorizationTests
{
    [Theory]
    [InlineData("/Identity/Account/Manage")]
    [InlineData("/Identity/Account/Manage/ChangePassword")]
    [InlineData("/Identity/Account/Manage/PersonalData")]
    public async Task ManagePages_ChallengeAnonymousUsers(string path)
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(path);

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/login",
            response.Headers.Location?.OriginalString ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignInPage_RemainsAnonymous()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/login");

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task IdentityRegisterPage_RemainsAnonymous()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Identity/Account/Register");

        // The page carries its own [AllowAnonymous], so it must not be challenged. It may
        // still refuse registration on its own terms; only a redirect to sign-in is a failure.
        Assert.NotEqual(System.Net.HttpStatusCode.Redirect, response.StatusCode);
    }
}
