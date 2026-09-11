using System.Net;
using System.Security.Claims;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Abuse;
using Glosify.Services.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Glosify.Tests;

public sealed class SignupAdmissionDurabilityTests
{
    [SqlServerFact]
    public Task LimitsSurviveNewConnections_AndResetOnUtcBoundaries() => SqlServerTestDatabase.RunAsync("signup_windows", async seed =>
    {
        var now = new DateTimeOffset(2026, 9, 11, 22, 59, 59, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var settings = new AbuseOptions { SignupsPerIpHour = 1, SignupsPerIpDay = 2, SignupsPerDay = 3 };
        async Task Admit(string ip)
        {
            await using var db = Context(seed);
            var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
            http.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse(ip);
            await ResourceAccounting.TransactionAsync(db, async () =>
            {
                await new SignupAdmissionService(db, http, Options.Create(settings), clock).AdmitAsync();
                await db.SaveChangesAsync(); return true;
            }, default);
        }
        await Admit("192.0.2.1");
        var hourly = await Assert.ThrowsAsync<SignupLimitException>(() => Admit("::ffff:192.0.2.1"));
        Assert.Equal(now.AddSeconds(1), hourly.RetryAt);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Admit("192.0.2.1");
        var daily = await Assert.ThrowsAsync<SignupLimitException>(() => Admit("192.0.2.1"));
        Assert.Equal(new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero), daily.RetryAt);
        await Admit("2001:db8::1");
        await Assert.ThrowsAsync<SignupLimitException>(() => Admit("192.0.2.2"));
        Assert.Equal(3, (await seed.Set<SignupBucket>().SingleAsync(x => x.Id.StartsWith("day:"))).Count);
        clock.Advance(TimeSpan.FromHours(1));
        await Admit("192.0.2.1");
        settings.SignupsEnabled = false;
        await Assert.ThrowsAsync<SignupLimitException>(() => Admit("192.0.2.9"));
        Assert.Equal(1, (await seed.Set<SignupBucket>().AsNoTracking().SingleAsync(x => x.Id == "day:" + clock.GetUtcNow().ToUnixTimeSeconds())).Count);
    });

    [SqlServerFact]
    public Task AccountAssociationAndAdmissionAreAtomic_AndDuplicateCallbacksConsumeOneSlot() => SqlServerTestDatabase.RunAsync("signup_atomic", async seed =>
    {
        var settings = new AbuseOptions { SignupsPerDay = 1 };
        async Task<ExternalAccountResolution> Resolve(bool failLogin)
        {
            await using var db = Context(seed);
            var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
            http.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
            var admission = new SignupAdmissionService(db, http, Options.Create(settings), TimeProvider.System);
            var service = new ExternalAccountService(new Store(db, failLogin), NullLogger<ExternalAccountService>.Instance, db, admission);
            var info = new ExternalLoginInfo(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Email, "owner@example.test")])), "Google", "provider-id", "Google");
            return await service.ResolveOrCreateAsync(info);
        }
        Assert.False((await Resolve(true)).Succeeded);
        Assert.Equal(0, await seed.Users.CountAsync());
        Assert.Equal(0, await seed.Set<SignupBucket>().CountAsync());
        var results = await Task.WhenAll(Resolve(false), Resolve(false));
        Assert.All(results, x => Assert.True(x.Succeeded));
        Assert.Equal(results[0].User!.Id, results[1].User!.Id);
        Assert.Equal(1, await seed.Users.CountAsync());
        Assert.Equal(1, await seed.UserLogins.CountAsync());
        Assert.All(await seed.Set<SignupBucket>().ToListAsync(), x => Assert.Equal(1, x.Count));
        settings.SignupsEnabled = false;
        Assert.True((await Resolve(false)).Succeeded);
    });

    private static GlosifyContext Context(GlosifyContext seed) => new(new DbContextOptionsBuilder<GlosifyContext>().UseSqlServer(seed.Database.GetConnectionString()).Options);
    private sealed class Store(GlosifyContext db, bool failLogin) : IExternalAccountUserStore
    {
        public async Task<ApplicationUser?> FindByLoginAsync(string provider, string key)
        {
            var login = await db.UserLogins.SingleOrDefaultAsync(x => x.LoginProvider == provider && x.ProviderKey == key);
            return login is null ? null : await db.Users.FindAsync(login.UserId);
        }
        public Task<ApplicationUser?> FindByEmailAsync(string email) => db.Users.SingleOrDefaultAsync(x => x.Email == email);
        public async Task<IdentityResult> CreateAsync(ApplicationUser user) { db.Add(user); await db.SaveChangesAsync(); return IdentityResult.Success; }
        public async Task<IdentityResult> AddLoginAsync(ApplicationUser user, ExternalLoginInfo info)
        {
            if (failLogin) return IdentityResult.Failed(new IdentityError { Code = "test-failure" });
            db.UserLogins.Add(new() { UserId = user.Id, LoginProvider = info.LoginProvider, ProviderKey = info.ProviderKey });
            await db.SaveChangesAsync(); return IdentityResult.Success;
        }
        public async Task<IdentityResult> DeleteAsync(ApplicationUser user) { db.Remove(user); await db.SaveChangesAsync(); return IdentityResult.Success; }
    }
}
