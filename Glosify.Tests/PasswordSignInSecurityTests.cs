using System.Net;
using AngleSharp.Html.Parser;
using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class PasswordSignInSecurityTests
{
    private const string Email = "password-signin@example.test";
    private const string Password = "PasswordSignIn1!";
    private const string ProtectedPath = "/Identity/Account/Manage";

    [Theory]
    [InlineData("/Identity/Account/Manage", true, "/Identity/Account/Manage")]
    [InlineData("/Identity/Account/Manage", false, "/Identity/Account/Manage")]
    [InlineData("https://attacker.example/", true, "/")]
    [InlineData("//attacker.example/", false, "/")]
    public async Task PasswordLogin_ContinuesToSecondFactorWithSafeReturnUrl(
        string returnUrl, bool rememberMe, string expectedReturnUrl)
    {
        using var factory = CreateFactory();
        await CreateUserAsync(factory);
        using var client = CreateClient(factory);

        var response = await LoginAsync(client, returnUrl, rememberMe);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        AssertNoApplicationCookie(factory, response);
        Assert.StartsWith("/Identity/Account/LoginWith2fa?", response.Headers.Location?.OriginalString);
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.OriginalString.Split('?', 2)[1]);
        Assert.Equal(expectedReturnUrl, query["returnUrl"]);
        Assert.Equal(rememberMe.ToString().ToLowerInvariant(), query["rememberMe"].ToString().ToLowerInvariant());
        var page = await client.GetAsync(response.Headers.Location);
        page.EnsureSuccessStatusCode();
        Assert.Contains("Input.TwoFactorCode", await page.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync(ProtectedPath)).StatusCode);
    }

    [Fact]
    public async Task PasswordLogin_RecoveryCodeCompletesSecondFactorBeforeProtectedAccess()
    {
        using var factory = CreateFactory();
        var recoveryCode = await CreateUserAsync(factory);
        using var client = CreateClient(factory);
        var login = await LoginAsync(client, ProtectedPath, false);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        AssertNoApplicationCookie(factory, login);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync(ProtectedPath)).StatusCode);
        var path = QueryHelpers.AddQueryString("/Identity/Account/LoginWithRecoveryCode", "returnUrl", ProtectedPath);
        var token = await AntiforgeryAsync(client, path);

        var response = await client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.RecoveryCode"] = recoveryCode,
            ["__RequestVerificationToken"] = token,
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(ProtectedPath, response.Headers.Location?.OriginalString);
        var protectedPage = await client.GetAsync(ProtectedPath);
        protectedPage.EnsureSuccessStatusCode();
        Assert.Contains(Email, await protectedPage.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("wrong-password")]
    [InlineData("locked")]
    [InlineData("unconfirmed")]
    public async Task PasswordLogin_RestrictedAttemptDoesNotStartSecondFactor(string restriction)
    {
        using var factory = CreateFactory(requireConfirmation: restriction == "unconfirmed");
        await CreateUserAsync(factory, restriction);
        using var client = CreateClient(factory);

        var response = await LoginAsync(client, ProtectedPath, false,
            restriction == "wrong-password" ? "WrongPassword1!" : Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("auth-error", await response.Content.ReadAsStringAsync());
        AssertNoApplicationCookie(factory, response);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync(ProtectedPath)).StatusCode);
    }

    private static async Task<HttpResponseMessage> LoginAsync(
        HttpClient client, string returnUrl, bool rememberMe, string password = Password)
    {
        var path = QueryHelpers.AddQueryString("/login", "returnUrl", returnUrl);
        var token = await AntiforgeryAsync(client, path);
        return await client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = Email,
            ["Password"] = password,
            ["RememberMe"] = rememberMe.ToString(),
            ["__RequestVerificationToken"] = token,
        }));
    }

    private static async Task<string> AntiforgeryAsync(HttpClient client, string path)
    {
        var document = new HtmlParser().ParseDocument(await client.GetStringAsync(path));
        return document.QuerySelector("input[name='__RequestVerificationToken']")!.GetAttribute("value")!;
    }

    private static async Task<string> CreateUserAsync(WebApplicationFactory<Program> factory, string? restriction = null)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = Email, Email = Email, EmailConfirmed = restriction != "unconfirmed" };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        Assert.True((await users.ResetAuthenticatorKeyAsync(user)).Succeeded);
        Assert.True((await users.SetTwoFactorEnabledAsync(user, true)).Succeeded);
        if (restriction == "locked")
            Assert.True((await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddHours(1))).Succeeded);
        return Assert.Single((await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 1))!);
    }

    private static WebApplicationFactory<Program> CreateFactory(bool requireConfirmation = false)
    {
        var databaseName = Guid.NewGuid().ToString("N");
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<GlosifyContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<GlosifyContext>>();
                services.AddDbContext<GlosifyContext>(options => options.UseInMemoryDatabase(databaseName));
                services.Configure<IdentityOptions>(options => options.SignIn.RequireConfirmedAccount = requireConfirmation);
            });
        });
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) => factory.CreateClient(new()
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
    });

    private static void AssertNoApplicationCookie(WebApplicationFactory<Program> factory, HttpResponseMessage response)
    {
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];
        Assert.DoesNotContain(cookies, cookie => cookie.StartsWith($"{options.Cookie.Name}=", StringComparison.Ordinal));
    }
}
