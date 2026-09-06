using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class TranslatorApiAuthenticationTests
{
    [Theory]
    [InlineData("/api/translator/translate")]
    [InlineData("/api/translator/saved-translations")]
    public async Task PostRequiresExplicitBearerTokenEvenWithAValidWebCookie(string endpoint)
    {
        // Keep the real cookie and bearer handlers and the real application pipeline.
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
            builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        using var scope = factory.Services.CreateScope();
        var cookies = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var cookie = $"{cookies.Cookie.Name}={cookies.TicketDataFormat.Protect(Ticket(IdentityConstants.ApplicationScheme))}";
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Scheme = "https";
        context.Request.Headers.Cookie = cookie;
        var authenticatedCookie = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        Assert.True(authenticatedCookie.Succeeded);
        Assert.Equal("translator-auth-test", authenticatedCookie.Principal?.FindFirstValue(ClaimTypes.NameIdentifier));

        using var anonymous = await client.PostAsJsonAsync(endpoint, new { });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        client.DefaultRequestHeaders.Add("Cookie", cookie);
        using var cookieOnly = await client.PostAsJsonAsync(endpoint, new { });
        Assert.Equal(HttpStatusCode.Unauthorized, cookieOnly.StatusCode);
        Assert.Null(cookieOnly.Headers.Location);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token");
        using var invalidBearer = await client.PostAsJsonAsync(endpoint, new { });
        Assert.Equal(HttpStatusCode.Unauthorized, invalidBearer.StatusCode);

        var bearer = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<BearerTokenOptions>>()
            .Get(IdentityConstants.BearerScheme);
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", bearer.BearerTokenProtector.Protect(Ticket(IdentityConstants.BearerScheme)));
        // Invalid input reaches model validation, not antiforgery rejection. No AI,
        // billing or database writes occur, and no antiforgery token is supplied.
        using var authorized = await client.PostAsJsonAsync(endpoint, new { });
        Assert.Equal(HttpStatusCode.BadRequest, authorized.StatusCode);
        Assert.Equal("application/problem+json", authorized.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await authorized.Content.ReadAsStringAsync());
        Assert.Equal("validation_failed", problem.RootElement.GetProperty("code").GetString());
        Assert.Contains("SourceText", problem.RootElement.GetProperty("errors").EnumerateObject().Select(property => property.Name));
    }

    private static AuthenticationTicket Ticket(string scheme) => new(
        new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "translator-auth-test")], scheme)),
        new AuthenticationProperties
        {
            IssuedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5),
        },
        scheme);
}
