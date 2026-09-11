using Glosify.Data;
using Glosify.Services.Ai;
using Glosify.Services.Speech;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Glosify.Tests;

public sealed class SpeechProviderBudgetTests
{
    [SqlServerFact]
    public Task CharacterCostReservesReleasesAndSettlesOnceUnderTheMonthlyCap() => SqlServerTestDatabase.RunAsync("speech_budget", async db =>
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
        var service = new SpeechProviderBudget(new Factory(db.Database.GetConnectionString()!), Options.Create(new AiUsageOptions
        {
            MonthlyBudget = new() { Enabled = true, LimitSek = 1m, ReservationSafetyMultiplier = 1.25m,
                Models = [new() { Deployment = "eleven_v3", TextSekPerMillionCharacters = 10_000m }] }
        }), clock);
        var reservation = await service.ReserveAsync(10, default);
        var budget = await db.AiMonthlyBudgets.AsNoTracking().SingleAsync();
        Assert.Equal("2026-09", budget.PeriodKey); Assert.Equal(125_000, budget.ReservedMicros); Assert.Equal(0, budget.SpentMicros);
        await service.SettleAsync(reservation, true, default);
        await service.SettleAsync(reservation, true, default);
        var rejected = await service.ReserveAsync(20, default);
        await service.SettleAsync(rejected, false, default);
        budget = await db.AiMonthlyBudgets.AsNoTracking().SingleAsync();
        Assert.Equal(0, budget.ReservedMicros); Assert.Equal(100_000, budget.SpentMicros);
        var error = await Assert.ThrowsAsync<PaidServicesBudgetExhaustedException>(() => service.ReserveAsync(100, default));
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 22, 0, 0, TimeSpan.Zero), error.ResetsAtUtc);
        Assert.Equal(2, await db.Set<SpeechBudgetReservation>().CountAsync());
        Assert.All(await db.Set<SpeechBudgetReservation>().ToListAsync(), x => Assert.True(x.Settled));
    });

    private sealed class Factory(string connection) : IDbContextFactory<GlosifyContext>
    {
        public GlosifyContext CreateDbContext() => new(new DbContextOptionsBuilder<GlosifyContext>().UseSqlServer(connection).Options);
        public Task<GlosifyContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
