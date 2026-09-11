using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Auth;
using Microsoft.AspNetCore.Authentication;
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

public sealed class ExternalSignInSecurityTests
{
    private const string Email = "external-signin@example.test";
    private const string ProviderKey = "external-signin-provider-key";
    private const string ReturnUrl = "/Identity/Account/Manage";
    private static readonly string Verifier = new('a', 43);

    [Theory]
    [InlineData("locked", true)]
    [InlineData("locked", false)]
    [InlineData("unconfirmed", true)]
    [InlineData("unconfirmed", false)]
    [InlineData("twofactor", true)]
    [InlineData("twofactor", false)]
    public async Task WebCallback_DoesNotBypassAccountRestrictions(string restriction, bool linked)
    {
        using var factory = CreateFactory(requireConfirmation: restriction == "unconfirmed");
        await CreateUserAsync(factory, linked, restriction);
        using var client = CreateClient(factory);
        AddExternalCookie(factory, client);

        var response = await client.GetAsync($"/Account/ExternalLoginCallback?returnUrl={ReturnUrl}");

        AssertNoApplicationCookie(factory, response);
        if (restriction == "twofactor")
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/Identity/Account/LoginWith2fa?", response.Headers.Location?.OriginalString);
            var query = QueryHelpers.ParseQuery(response.Headers.Location!.OriginalString.Split('?', 2)[1]);
            Assert.Equal(ReturnUrl, query["returnUrl"]);
            var twoFactorPage = await client.GetAsync(response.Headers.Location);
            twoFactorPage.EnsureSuccessStatusCode();
            Assert.Contains("Input.TwoFactorCode", await twoFactorPage.Content.ReadAsStringAsync());
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("auth-error", await response.Content.ReadAsStringAsync());
        }

        client.DefaultRequestHeaders.Remove("Cookie");
        var protectedPage = await client.GetAsync(ReturnUrl);
        Assert.Equal(HttpStatusCode.Redirect, protectedPage.StatusCode);
        Assert.StartsWith("/login?", protectedPage.Headers.Location?.PathAndQuery);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WebCallback_SecondFactorCanCompleteBeforeProtectedAccess(bool linked)
    {
        using var factory = CreateFactory();
        var userId = await CreateUserAsync(factory, linked, "twofactor");
        string recoveryCode;
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByIdAsync(userId);
            recoveryCode = Assert.Single((await users.GenerateNewTwoFactorRecoveryCodesAsync(user!, 1))!);
        }
        using var client = CreateClient(factory);
        AddExternalCookie(factory, client);
        var callback = await client.GetAsync($"/Account/ExternalLoginCallback?returnUrl={ReturnUrl}");
        AssertNoApplicationCookie(factory, callback);
        client.DefaultRequestHeaders.Remove("Cookie");
        var recoveryPath = $"/Identity/Account/LoginWithRecoveryCode?returnUrl={ReturnUrl}";
        var page = await client.GetStringAsync(recoveryPath);
        var document = new HtmlParser().ParseDocument(page);
        var antiforgery = document.QuerySelector("input[name='__RequestVerificationToken']")!.GetAttribute("value")!;

        var response = await client.PostAsync(recoveryPath, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.RecoveryCode"] = recoveryCode,
            ["__RequestVerificationToken"] = antiforgery,
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(ReturnUrl, response.Headers.Location?.OriginalString);
        var protectedPage = await client.GetAsync(ReturnUrl);
        protectedPage.EnsureSuccessStatusCode();
        Assert.Contains(Email, await protectedPage.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("existing")]
    [InlineData("link")]
    [InlineData("new")]
    public async Task WebCallback_UnrestrictedAccountsStillSignIn(string account)
    {
        using var factory = CreateFactory();
        if (account != "new")
            await CreateUserAsync(factory, linked: account == "existing");
        using var client = CreateClient(factory);
        AddExternalCookie(factory, client);

        var response = await client.GetAsync($"/Account/ExternalLoginCallback?returnUrl={ReturnUrl}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(ReturnUrl, response.Headers.Location?.OriginalString);
        client.DefaultRequestHeaders.Remove("Cookie");
        var protectedPage = await client.GetAsync(ReturnUrl);
        protectedPage.EnsureSuccessStatusCode();
        Assert.Contains(Email, await protectedPage.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MobileSignupDenialReturnsToAppWithRetryMetadata(bool killSwitch)
    {
        using var factory = CreateFactory(signupsEnabled: !killSwitch);
        if (!killSwitch)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GlosifyContext>();
            var day = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
            db.Add(new Glosify.Services.Abuse.SignupBucket { Id = "day:" + day.ToUnixTimeSeconds(), Count = 200, ExpiresAt = day.AddDays(1) });
            await db.SaveChangesAsync();
        }
        using var client = CreateClient(factory);
        AddExternalCookie(factory, client);
        using var response = await client.GetAsync("/api/auth/external/google/callback");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("glosify", response.Headers.Location?.Scheme);
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.NotEmpty(query["error"].ToString());
        Assert.False(query.ContainsKey("code"));
        Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        Assert.Equal(((long)response.Headers.RetryAfter!.Delta!.Value.TotalSeconds).ToString(), query["retryAfter"]);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var verify = factory.Services.CreateScope();
        Assert.Empty(await verify.ServiceProvider.GetRequiredService<GlosifyContext>().Users.ToListAsync());
        AssertNoApplicationCookie(factory, response);
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("unconfirmed")]
    [InlineData("twofactor")]
    public async Task MobileCallback_DoesNotIssueCodesForRestrictedAccounts(string restriction)
    {
        using var factory = CreateFactory(requireConfirmation: restriction == "unconfirmed");
        await CreateUserAsync(factory, linked: true, restriction);
        using var client = CreateClient(factory);
        AddExternalCookie(factory, client);

        var response = await client.GetAsync("/api/auth/external/google/callback");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("glosify", response.Headers.Location?.Scheme);
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.True(query.ContainsKey("error"));
        Assert.False(query.ContainsKey("code"));
        AssertNoApplicationCookie(factory, response);
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("unconfirmed")]
    [InlineData("twofactor")]
    public async Task MobileExchange_RechecksRestrictionsChangedAfterCodeIssuance(string restriction)
    {
        using var factory = CreateFactory(requireConfirmation: restriction == "unconfirmed");
        var userId = await CreateUserAsync(factory, linked: true);
        using var client = CreateClient(factory);
        AddExternalCookie(factory, client);
        var callback = await client.GetAsync("/api/auth/external/google/callback");
        var code = QueryHelpers.ParseQuery(callback.Headers.Location!.Query)["code"].ToString();
        Assert.NotEmpty(code);
        client.DefaultRequestHeaders.Remove("Cookie");
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByIdAsync(userId);
            await RestrictAsync(users, user!, restriction);
        }

        var response = await client.PostAsJsonAsync("/api/auth/external/exchange", new { code, codeVerifier = Verifier });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unauthorized", json.RootElement.GetProperty("code").GetString());
        Assert.False(json.RootElement.TryGetProperty("accessToken", out _));
        AssertNoApplicationCookie(factory, response);
    }

    [Fact]
    public async Task MobileExchange_UnrestrictedAccountStillReceivesUsableBearerTokens()
    {
        using var factory = CreateFactory();
        await CreateUserAsync(factory, linked: true);
        using var client = CreateClient(factory);
        AddExternalCookie(factory, client);
        var callback = await client.GetAsync("/api/auth/external/google/callback");
        var code = QueryHelpers.ParseQuery(callback.Headers.Location!.Query)["code"].ToString();
        Assert.NotEmpty(code);
        client.DefaultRequestHeaders.Remove("Cookie");

        var response = await client.PostAsJsonAsync("/api/auth/external/exchange", new { code, codeVerifier = Verifier });

        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("refreshToken").GetString()));
        client.DefaultRequestHeaders.Authorization = new("Bearer", json.RootElement.GetProperty("accessToken").GetString());
        var profile = await client.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal(Email, profile.GetProperty("email").GetString());
    }

    private static WebApplicationFactory<Program> CreateFactory(bool requireConfirmation = false, bool signupsEnabled = true)
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
                services.Configure<Glosify.Services.Abuse.AbuseOptions>(options =>
                {
                    options.SignupsEnabled = signupsEnabled;
                    options.SignupHashKey = SignupAdmissionTests.HashKey;
                });
            });
        });
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) => factory.CreateClient(new()
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
    });

    private static async Task<string> CreateUserAsync(WebApplicationFactory<Program> factory, bool linked, string? restriction = null)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = Email, Email = Email, EmailConfirmed = true };
        Assert.True((await users.CreateAsync(user, "ExternalSignIn1!")).Succeeded);
        if (linked)
            Assert.True((await users.AddLoginAsync(user, new UserLoginInfo("Google", ProviderKey, "Google"))).Succeeded);
        if (restriction is not null)
            await RestrictAsync(users, user, restriction);
        return user.Id;
    }

    private static async Task RestrictAsync(UserManager<ApplicationUser> users, ApplicationUser user, string restriction)
    {
        switch (restriction)
        {
            case "locked":
                Assert.True((await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddHours(1))).Succeeded);
                break;
            case "unconfirmed":
                user.EmailConfirmed = false;
                Assert.True((await users.UpdateAsync(user)).Succeeded);
                break;
            case "twofactor":
                Assert.True((await users.ResetAuthenticatorKeyAsync(user)).Succeeded);
                Assert.True((await users.SetTwoFactorEnabledAsync(user, true)).Succeeded);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(restriction));
        }
    }

    private static void AddExternalCookie(WebApplicationFactory<Program> factory, HttpClient client)
    {
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ExternalScheme);
        var properties = new AuthenticationProperties
        {
            IssuedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5),
        };
        properties.Items["LoginProvider"] = "Google";
        properties.Items["glosify:pkce_challenge"] = Pkce.CreateChallenge(Verifier);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, ProviderKey), new Claim(ClaimTypes.Email, Email)], IdentityConstants.ExternalScheme));
        var ticket = new AuthenticationTicket(principal, properties, IdentityConstants.ExternalScheme);
        client.DefaultRequestHeaders.Add("Cookie", $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}");
    }

    private static void AssertNoApplicationCookie(WebApplicationFactory<Program> factory, HttpResponseMessage response)
    {
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];
        Assert.DoesNotContain(cookies, cookie => cookie.StartsWith($"{options.Cookie.Name}=", StringComparison.Ordinal));
    }
}
