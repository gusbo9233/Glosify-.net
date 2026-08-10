using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class PaidServiceGateTests
{
    [Fact]
    public async Task ExhaustedPeriodClosesPaidFeaturesAndNextStockholmMonthReopensThem()
    {
        await using var context = new GlosifyContext(
            new DbContextOptionsBuilder<GlosifyContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
        context.AiMonthlyBudgets.Add(new AiMonthlyBudget
        {
            PeriodKey = "2026-07",
            LimitMicros = 300_000_000,
            ExhaustedAt = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero),
            ExhaustedReason = PaidServiceGate.BudgetExhaustedReason,
            CreatedAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero),
        });
        await context.SaveChangesAsync();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));
        var gate = new PaidServiceGate(context, Options.Create(CreateOptions()), clock);

        var closed = await gate.GetStatusAsync();

        Assert.False(closed.Available);
        Assert.Equal(PaidServiceGate.BudgetExhaustedReason, closed.Reason);
        Assert.Equal(new DateTimeOffset(2026, 7, 31, 22, 0, 0, TimeSpan.Zero), closed.ResetsAtUtc);
        await Assert.ThrowsAsync<PaidServicesBudgetExhaustedException>(() => gate.EnsureAvailableAsync());

        clock.Now = new DateTimeOffset(2026, 7, 31, 22, 0, 0, TimeSpan.Zero);
        var reopened = await gate.GetStatusAsync();
        Assert.True(reopened.Available);
        Assert.Null(reopened.Reason);
    }

    private static AiUsageOptions CreateOptions() => new()
    {
        MonthlyBudget = new AiMonthlyBudgetOptions
        {
            Enabled = true,
            LimitSek = 300,
            TimeZoneId = "Europe/Stockholm",
        },
    };

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
