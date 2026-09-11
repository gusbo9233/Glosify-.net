using System.Net;
using AngleSharp.Html.Parser;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.RealtimeTranslation;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class AdministratorRegistrationSecurityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PasswordSignupIsClosed_AndExistingFormerAdminEmailCannotAcquireGrant(bool confirmEmail)
    {
        const string email = "formerly-allowlisted@example.test";
        const string approvedId = "operator-approved-existing-account";
        var databaseName = Guid.NewGuid().ToString("N");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Admin:Emails:0"] = email,
                    ["Admin:UserIds:0"] = approvedId,
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<GlosifyContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<GlosifyContext>>();
                services.AddDbContext<GlosifyContext>(options => options.UseInMemoryDatabase(databaseName));
            });
        });
        using var client = factory.CreateClient(new()
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });
        var registerPage = new HtmlParser().ParseDocument(await client.GetStringAsync("/login"));
        var token = registerPage.QuerySelector("input[name='__RequestVerificationToken']")!.GetAttribute("value")!;

        var registration = await client.PostAsync("/Account/Register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = "Registration1!",
            ["ConfirmPassword"] = "Registration1!",
            ["Id"] = approvedId,
            ["__RequestVerificationToken"] = token,
        }));

        Assert.Equal(HttpStatusCode.Forbidden, registration.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.Null(await users.FindByEmailAsync(email));
            var user = new ApplicationUser { UserName = email, Email = email };
            Assert.True((await users.CreateAsync(user, "Registration1!")).Succeeded);
            Assert.NotEqual(approvedId, user.Id);
            Assert.False(user.EmailConfirmed);
            if (confirmEmail)
            {
                var confirmation = await users.GenerateEmailConfirmationTokenAsync(user);
                Assert.True((await users.ConfirmEmailAsync(user, confirmation)).Succeeded);
            }
            var captures = scope.ServiceProvider.GetRequiredService<IRealtimeTranslationCaptureService>();
            Assert.False(await captures.IsAdminUserAsync(user.Id));
        }
        var login = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email, ["Password"] = "Registration1!", ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var profile = await client.GetAsync("/Identity/Account/Manage");
        profile.EnsureSuccessStatusCode();
        Assert.Contains(email, await profile.Content.ReadAsStringAsync());
        foreach (var path in new[] { "/Admin/AiCredits", "/Admin/TranslationCaptures" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var cookieOptions = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(IdentityConstants.ApplicationScheme);
            Assert.Equal(cookieOptions.AccessDeniedPath.Value, response.Headers.Location?.AbsolutePath);
            Assert.Equal(path, Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(
                response.Headers.Location!.Query)[cookieOptions.ReturnUrlParameter]);
        }
        var document = new HtmlParser().ParseDocument(await profile.Content.ReadAsStringAsync());
        Assert.Empty(document.QuerySelectorAll("a[href='/Admin/AiCredits']"));
    }
}
