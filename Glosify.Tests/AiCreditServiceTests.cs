using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;
using Glosify.Services.Ai;

namespace Glosify.Tests;

public sealed class AiCreditServiceTests
{
    [Fact]
    public async Task GetOrCreateAccount_AppliesTrialGrantOnce()
    {
        await using var context = CreateContext();
        context.Users.Add(new ApplicationUser { Id = "user-1", Email = "user@example.test", UserName = "user@example.test" });
        await context.SaveChangesAsync();
        var service = CreateService(context);

        var first = await service.GetOrCreateAccountAsync("user-1");
        var second = await service.GetOrCreateAccountAsync("user-1");

        Assert.Equal(25, first.AvailableCredits);
        Assert.Equal(25, second.AvailableCredits);
        Assert.Single(await context.AiCreditTransactions.Where(t => t.Kind == AiCreditTransactionKinds.TrialGrant).ToListAsync());
    }

    [Fact]
    public async Task Grant_AddsCreditsAndWritesActorNote()
    {
        await using var context = CreateContext();
        context.Users.Add(new ApplicationUser { Id = "user-1", Email = "user@example.test", UserName = "user@example.test" });
        await context.SaveChangesAsync();
        var service = CreateService(context);

        await service.GrantAsync("admin-1", "user-1", 10, "Manual top-up");

        var account = await service.GetOrCreateAccountAsync("user-1");
        var grant = await context.AiCreditTransactions.SingleAsync(t => t.Kind == AiCreditTransactionKinds.AdminGrant);
        Assert.Equal(35, account.BalanceCredits);
        Assert.Equal("admin-1", grant.ActorUserId);
        Assert.Equal("Manual top-up", grant.Note);
    }

    [Fact]
    public async Task Reserve_BlocksWhenAvailableCreditsAreTooLow()
    {
        await using var context = CreateContext();
        context.Users.Add(new ApplicationUser { Id = "user-1", Email = "user@example.test", UserName = "user@example.test" });
        await context.SaveChangesAsync();
        var service = CreateService(context);

        var ex = await Assert.ThrowsAsync<InsufficientAiCreditsException>(() =>
            service.ReserveAsync(UsageContext("user-1"), "gemini", "test-model", 26_000));

        Assert.Equal(25, ex.AvailableCredits);
        Assert.Equal(26, ex.RequiredCredits);
    }

    [Fact]
    public async Task CommitUsage_DebitsRoundedCreditsAndReleasesUnusedReserve()
    {
        await using var context = CreateContext();
        context.Users.Add(new ApplicationUser { Id = "user-1", Email = "user@example.test", UserName = "user@example.test" });
        await context.SaveChangesAsync();
        var service = CreateService(context);

        var reservation = await service.ReserveAsync(UsageContext("user-1"), "gemini", "test-model", 2_500);
        await service.CommitUsageAsync(reservation.ReservationId, new AiTokenUsage(900, 200, 0, 0, 1_100));

        var account = await service.GetOrCreateAccountAsync("user-1");
        Assert.Equal(23, account.BalanceCredits);
        Assert.Equal(0, account.ReservedCredits);
        Assert.Equal(2, (await context.AiCreditTransactions.SingleAsync(t => t.Kind == AiCreditTransactionKinds.UsageDebit)).CreditAmount * -1);
        Assert.Single(await context.AiCreditTransactions.Where(t => t.Kind == AiCreditTransactionKinds.Release).ToListAsync());
    }

    [Fact]
    public async Task Release_ReturnsReservedCreditsWithoutDebit()
    {
        await using var context = CreateContext();
        context.Users.Add(new ApplicationUser { Id = "user-1", Email = "user@example.test", UserName = "user@example.test" });
        await context.SaveChangesAsync();
        var service = CreateService(context);

        var reservation = await service.ReserveAsync(UsageContext("user-1"), "gemini", "test-model", 2_000);
        await service.ReleaseAsync(reservation.ReservationId);

        var account = await service.GetOrCreateAccountAsync("user-1");
        Assert.Equal(25, account.BalanceCredits);
        Assert.Equal(0, account.ReservedCredits);
        Assert.Empty(await context.AiCreditTransactions.Where(t => t.Kind == AiCreditTransactionKinds.UsageDebit).ToListAsync());
        Assert.Single(await context.AiCreditTransactions.Where(t => t.Kind == AiCreditTransactionKinds.Release).ToListAsync());
    }

    private static GlosifyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new GlosifyContext(options);
    }

    private static AiCreditService CreateService(GlosifyContext context)
    {
        return new AiCreditService(context, Options.Create(new AiUsageOptions
        {
            TrialGrantCredits = 25,
            CreditsPerThousandTokens = 1
        }));
    }

    private static AiUsageContext UsageContext(string userId) =>
        new(userId, AiUsageFeatures.Assistant, "test", Guid.NewGuid());
}
