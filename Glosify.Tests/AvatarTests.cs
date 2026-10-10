using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Avatar;
using Glosify.Services.RealtimeTranslation;
using Glosify.Services.Speech;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Glosify.Tests;

public sealed class AvatarTests
{
    [Theory]
    [InlineData(null, 401)]
    [InlineData("learner", 403)]
    [InlineData("admin", 200)]
    public async Task ConfigRequiresAdmin(string? user, int expected)
    {
        using var app = new AvatarFixture(); using var client = await app.Client(user);
        var response = await client.GetAsync("/api/avatar/config");
        Assert.Equal(expected, (int)response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret-for-tests", text);
        if (expected != 200) Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task DisabledFeatureDoesNotExposePageOrConfig()
    {
        using var app = new AvatarFixture(false); using var client = await app.Client("admin");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Avatar")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/avatar/config")).StatusCode);
    }

    [Fact]
    public async Task StartRequiresAntiforgeryAndProtectsQuizOwnership()
    {
        using var app = new AvatarFixture(); using var client = await app.Client("admin");
        var foreign = Guid.NewGuid(); var empty = Guid.NewGuid();
        await app.Seed(async db =>
        {
            db.Quizzes.Add(new Quiz { Id = foreign, UserId = "learner", Name = "Private", TargetLanguage = "Swedish" });
            db.Quizzes.Add(new Quiz { Id = empty, UserId = "admin", Name = "Empty", TargetLanguage = "Swedish" });
            await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/avatar/sessions", new { language = "en" })).StatusCode);
        await app.Antiforgery(client);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/avatar/sessions", new { quizId = foreign })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/avatar/sessions", new { quizId = empty })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/avatar/sessions", new { language = "free" })).StatusCode);
        var response = await client.PostAsJsonAsync("/api/avatar/sessions", new { language = "sv" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/avatar/sessions", new { language = "sv" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/api/avatar/sessions/{session}/end", new { })).StatusCode);
        // The browser may finish closing the socket before its cleanup POST arrives.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/api/avatar/sessions/{session}/end", new { })).StatusCode);
    }

    [Fact]
    public async Task QuizSetsLanguageAndWebSocketRejectsForeignOrigin()
    {
        using var app = new AvatarFixture(); using var client = await app.Client("admin"); await app.Antiforgery(client);
        var quiz = Guid.NewGuid();
        await app.Seed(async db =>
        {
            db.Quizzes.Add(new Quiz { Id = quiz, UserId = "admin", Name = "Coffee", TargetLanguage = "Swedish" });
            db.Words.Add(new Word { Id = "coffee", QuizId = quiz, Lemma = "kaffe", Translation = "coffee" });
            await db.SaveChangesAsync();
        });
        var response = await client.PostAsJsonAsync("/api/avatar/sessions", new { quizId = quiz, language = "en" });
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("sv", payload.GetProperty("language").GetString());
        var id = payload.GetProperty("sessionId").GetGuid();
        var sessions = app.Services.GetRequiredService<AvatarSessions>();
        Assert.Contains("kaffe", sessions.Get(id, "admin").Practice);
        Assert.Equal(404, Assert.Throws<AvatarException>(() => sessions.Get(id, "learner")).Status);
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/avatar/sessions/{id}/voice")).StatusCode);
    }

    [Fact]
    public async Task NavigationOnlyOffersAvatarToAdmins()
    {
        using var app = new AvatarFixture();
        using var admin = await app.Client("admin"); using var learner = await app.Client("learner");
        var page = await admin.GetAsync("/Avatar");
        Assert.Contains("microphone=(self)", page.Headers.GetValues("Permissions-Policy").Single());
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("href=\"/Avatar\"", html);
        Assert.Equal(HttpStatusCode.Forbidden, (await learner.GetAsync("/Avatar")).StatusCode);
        var response = await learner.GetAsync("/Quiz");
        Assert.DoesNotContain("href=\"/Avatar\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void SessionsExpireAndCannotBeReusedAcrossAccounts()
    {
        var clock = new FakeTimeProvider(); var sessions = new AvatarSessions(clock);
        var language = Glosify.Services.Language.QuizLanguageCatalog.Find("en")!;
        var first = sessions.Create("admin", language, "free");
        Assert.Equal(404, Assert.Throws<AvatarException>(() => sessions.Get(first.Id, "another")).Status);
        Assert.Equal(409, Assert.Throws<AvatarException>(() => sessions.Create("admin", language, "free")).Status);
        clock.Advance(TimeSpan.FromSeconds(31)); sessions.Sweep();
        Assert.True(first.Lifetime.IsCancellationRequested);
        Assert.NotEqual(first.Id, sessions.Create("admin", language, "free").Id);
    }
}

internal sealed class AvatarFixture : WebApplicationFactory<Program>
{
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly bool _enabled;
    public Action<IServiceCollection>? Overrides { get; set; }
    public AvatarFixture(bool enabled = true) => _enabled = enabled;
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseStaticWebAssets();
        builder.UseSetting("SharedAuth:Enabled", "false");
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Avatar:Enabled"] = _enabled.ToString(), ["Admin:UserIds:0"] = "admin",
            ["Speech:ApiKey"] = "secret-for-tests", ["OPENAI_SECRET_KEY"] = "secret-for-tests",
            ["AiUsage:MonthlyBudget:Enabled"] = "false"
        }));
        builder.ConfigureTestServices(services =>
        {
            foreach (var item in services.Where(s => s.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)).ToArray()) services.Remove(item);
            services.RemoveAll<DbContextOptions<GlosifyContext>>(); services.RemoveAll<IDbContextOptionsConfiguration<GlosifyContext>>();
            services.AddDbContext<GlosifyContext>(o => o.UseInMemoryDatabase(_database));
            Overrides?.Invoke(services);
        });
    }
    public async Task Seed(Func<GlosifyContext, Task> seed)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GlosifyContext>();
        db.AccountingBypass = true;
        await seed(db);
    }
    public async Task<HttpClient> Client(string? user)
    {
        var client = CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
        if (user is null) return client;
        await Seed(async db =>
        {
            if (!await db.Set<Glosify.Services.Abuse.ResourceAccountingState>().AnyAsync())
                db.Add(new Glosify.Services.Abuse.ResourceAccountingState { Id = 1, Ready = true });
            if (!await db.Users.AnyAsync(x => x.Id == user))
            {
                db.Users.Add(new ApplicationUser { Id = user, UserName = user, SecurityStamp = "stamp", EmailConfirmed = true });
                db.AiCreditAccounts.Add(new AiCreditAccount { UserId = user, BalanceCredits = 100, TrialGrantedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
            }
        });
        var options = Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("Identity.Application");
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user), new Claim("AspNet.Identity.SecurityStamp", "stamp")], "Identity.Application"));
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) }, "Identity.Application");
        client.DefaultRequestHeaders.Add("Cookie", options.Cookie.Name + "=" + options.TicketDataFormat.Protect(ticket));
        return client;
    }
    public async Task Antiforgery(HttpClient client)
    {
        var response = await client.GetAsync("/Avatar");
        var doc = new HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add("RequestVerificationToken", doc.QuerySelector("input[name='__RequestVerificationToken']")!.GetAttribute("value"));
        var cookies = client.DefaultRequestHeaders.GetValues("Cookie").Single() + "; " + string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(x => x.Split(';')[0]));
        client.DefaultRequestHeaders.Remove("Cookie"); client.DefaultRequestHeaders.Add("Cookie", cookies);
    }
}
