using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai;
using Glosify.Services.Auth;
using Glosify.Services.RealtimeTranslation;
using Glosify.Services.Speech;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Glosify.Tests;

public sealed class FractionalCreditTests
{
    [Theory]
    [InlineData("9.8", 10)]
    [InlineData("0.2", 1)]
    [InlineData("-0.2", -1)]
    [InlineData("-9.8", -10)]
    [InlineData("0", 0)]
    [InlineData("2147483647.000001", int.MaxValue)]
    [InlineData("9999999999999.999999", int.MaxValue)]
    [InlineData("-2147483648.000001", int.MinValue)]
    [InlineData("-9999999999999.999999", int.MinValue)]
    public void DisplaysWholeCreditsWithoutChangingTheAmount(string value, int expected)
        => Assert.Equal(expected, CreditAmounts.Display(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void PricingUsesActualTokensAndCharactersWithMicrocreditPrecision()
    {
        var pricing = Pricing();
        Assert.Equal(0, pricing.CalculateTokenCredits(0, AiUsageFeatures.Assistant, "test"));
        Assert.Equal(0.00008m, pricing.CalculateTokenCredits(1, AiUsageFeatures.Assistant, "test"));
        Assert.Equal(0.08008m, pricing.CalculateTokenCredits(1001, AiUsageFeatures.Assistant, "test"));
        Assert.Equal(0.000001m, CreditAmounts.RoundCharge(0.00000001m));
        var speech = new SpeechOptions();
        Assert.Equal(0m, speech.CalculateCredits(0));
        Assert.Equal(0.364517m, speech.CalculateCredits(20));
        Assert.Equal(3.645161m, speech.CalculateCredits(200));
        Assert.Equal(4, speech.MaximumSegmentCredits);
    }

    [SqlServerFact]
    public Task LastFractionalBalanceIsReservedOnceAndSettlementIsIdempotent() => SqlServerTestDatabase.RunAsync("fractional_concurrency", async db =>
    {
        db.Users.Add(new ApplicationUser { Id = "speaker", UserName = "speaker" });
        db.AiCreditAccounts.Add(new AiCreditAccount { UserId = "speaker", BalanceCredits = 0.2m });
        await db.SaveChangesAsync();
        var factory = new Factory(db.Database.GetConnectionString()!);
        async Task<Guid?> Reserve()
        {
            await using var context = factory.CreateDbContext();
            try { return await Service(context, factory).ReserveSpeechAsync("speaker", 0.2m); }
            catch (InsufficientAiCreditsException) { return null; }
        }
        var results = await Task.WhenAll(Reserve(), Reserve());
        var id = Assert.Single(results, x => x.HasValue)!.Value;
        Assert.Single(results, x => !x.HasValue);
        await using var isolated = factory.CreateDbContext();
        var service = Service(isolated, factory);
        Assert.Equal(0, (await service.GetOrCreateAccountAsync("speaker")).AvailableCredits);
        async Task Commit()
        {
            await using var context = factory.CreateDbContext();
            await Service(context, factory).CommitSpeechAsync(id);
        }
        await Task.WhenAll(Commit(), Commit());
        isolated.ChangeTracker.Clear();
        await service.CommitSpeechAsync(id);
        await service.ReleaseAsync(id);
        var account = await service.GetOrCreateAccountAsync("speaker");
        Assert.Equal(0, account.BalanceCredits);
        Assert.Equal(0, account.ReservedCredits);
        var debit = Assert.Single(await db.AiCreditTransactions.AsNoTracking().Where(x => x.Kind == AiCreditTransactionKinds.UsageDebit).ToListAsync());
        Assert.Equal(-0.2m, debit.CreditAmount);
        Assert.Equal("elevenlabs", debit.Provider);
        Assert.Equal("eleven_v3", debit.Model);
    });

    [SqlServerFact]
    public Task UnderestimatedTokensCannotSpendAnotherReservation() => SqlServerTestDatabase.RunAsync("fractional_underestimate", async db =>
    {
        db.Users.Add(new ApplicationUser { Id = "speaker", UserName = "speaker" });
        db.AiCreditAccounts.Add(new AiCreditAccount { UserId = "speaker", BalanceCredits = 0.2m });
        await db.SaveChangesAsync();
        var factory = new Factory(db.Database.GetConnectionString()!);
        var service = Service(db, factory);
        var first = await service.ReserveAsync(new AiUsageContext("speaker", AiUsageFeatures.Assistant, "test", Guid.NewGuid()), "openai", "test", 1250);
        Assert.Equal(0.1m, first.ReservedCredits);
        var other = await service.ReserveSpeechAsync("speaker", 0.1m);
        await service.CommitUsageAsync(first.ReservationId, new AiTokenUsage(1300, 75, 0, 0, 1375));
        await service.CommitUsageAsync(first.ReservationId, new AiTokenUsage(1300, 75, 0, 0, 1375));
        var account = await service.GetOrCreateAccountAsync("speaker");
        Assert.Equal(0.1m, account.BalanceCredits);
        Assert.Equal(0.1m, account.ReservedCredits);
        Assert.Equal(0m, account.AvailableCredits);
        var debit = await db.AiCreditTransactions.SingleAsync(x => x.Kind == AiCreditTransactionKinds.UsageDebit);
        Assert.Equal(-0.1m, debit.CreditAmount);
        Assert.Equal(1375, debit.TotalTokens);
        Assert.Contains("capped", debit.Note);
        await service.CommitSpeechAsync(other);
        db.ChangeTracker.Clear();
        Assert.Equal(0m, (await service.GetOrCreateAccountAsync("speaker")).BalanceCredits);
    });

    [SqlServerFact]
    public Task TinyDebitsAndExpiredReservationsPreserveExactBalancesAcrossContexts() => SqlServerTestDatabase.RunAsync("fractional_expiry", async db =>
    {
        db.Users.Add(new ApplicationUser { Id = "speaker", UserName = "speaker" });
        db.AiCreditAccounts.Add(new AiCreditAccount { UserId = "speaker", BalanceCredits = 0.2m });
        await db.SaveChangesAsync();
        var factory = new Factory(db.Database.GetConnectionString()!);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        for (var i = 0; i < 10; i++)
        {
            await using var context = factory.CreateDbContext();
            var service = Service(context, factory, clock);
            var id = await service.ReserveSpeechAsync("speaker", 0.000001m);
            await service.CommitSpeechAsync(id);
        }
        await using var fresh = factory.CreateDbContext();
        var credits = Service(fresh, factory, clock);
        Assert.Equal(0.19999m, (await credits.GetOrCreateAccountAsync("speaker")).BalanceCredits);
        var abandoned = await credits.ReserveSpeechAsync("speaker", 0.1m);
        clock.Advance(TimeSpan.FromMinutes(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => credits.CommitSpeechAsync(abandoned));
        await Assert.ThrowsAsync<InvalidOperationException>(() => credits.CommitSpeechAsync(abandoned));
        var account = await credits.GetOrCreateAccountAsync("speaker");
        Assert.Equal(0.19999m, account.AvailableCredits);
        Assert.Equal(0, account.ReservedCredits);
        Assert.Single(await db.AiCreditTransactions.Where(x => x.ReservationId == abandoned && x.Kind == AiCreditTransactionKinds.Release).ToListAsync());
    });

    [SqlServerFact]
    public Task IntegerApiDisplayDoesNotAuthorizeWholeCreditSpending() => SqlServerTestDatabase.RunAsync("fractional_display", async db =>
    {
        db.Users.Add(new ApplicationUser { Id = "speaker", UserName = "speaker" });
        db.AiCreditAccounts.Add(new AiCreditAccount { UserId = "speaker", BalanceCredits = 0.2m });
        await db.SaveChangesAsync();
        var service = Service(db, new Factory(db.Database.GetConnectionString()!));
        var controller = new Glosify.Controllers.Api.MeApiController(service)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                        [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "speaker")], "test"))
                }
            }
        };
        var response = await controller.Get(default);
        var dto = Assert.IsType<Glosify.Models.Api.MeDto>(Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(response.Result).Value);
        Assert.Equal(1, dto.AvailableCredits);
        await Assert.ThrowsAsync<InsufficientAiCreditsException>(() => service.ReserveSpeechAsync("speaker", 1m));
        Assert.Empty(await db.AiCreditTransactions.ToListAsync());
        var id = await service.ReserveSpeechAsync("speaker", 0.2m);
        await service.ReleaseAsync(id);
        Assert.Equal(0.2m, (await service.GetOrCreateAccountAsync("speaker")).AvailableCredits);
    });

    [SqlServerFact]
    public Task MigrationPreservesWholeAmountsAndDoesNotPermitDowngrade() => SqlServerTestDatabase.RunAsync("fractional_migration", async db =>
    {
        await db.Database.EnsureDeletedAsync();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260911151434_DurableAbuseControls");
        db.Users.Add(new ApplicationUser { Id = "legacy", UserName = "legacy" });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO AiCreditAccounts(UserId,BalanceCredits,ReservedCredits,CreatedAt,UpdatedAt)
            VALUES ('legacy',2147483647,17,SYSUTCDATETIME(),SYSUTCDATETIME());
            INSERT INTO AiCreditTransactions(Id,UserId,Kind,CreditAmount,BalanceAfterCredits,ReservedAfterCredits,CreatedAt,Note)
            VALUES ('10000000-0000-0000-0000-000000000001','legacy','usage_debit',-17,2147483647,17,SYSUTCDATETIME(),'preserve me');
            """);
        await db.Database.OpenConnectionAsync();
        var connection = (Microsoft.Data.SqlClient.SqlConnection)db.Database.GetDbConnection();
        var fingerprint = await CreditLedgerDeploymentCheck.FingerprintAsync(connection);
        await migrator.MigrateAsync();
        Assert.Equal(fingerprint, await CreditLedgerDeploymentCheck.FingerprintAsync(connection));
        var account = await db.AiCreditAccounts.SingleAsync();
        Assert.Equal(2147483647m, account.BalanceCredits);
        Assert.Equal(17m, account.ReservedCredits);
        var transaction = await db.AiCreditTransactions.SingleAsync();
        Assert.Equal(-17m, transaction.CreditAmount);
        Assert.Equal(2147483647m, transaction.BalanceAfterCredits);
        Assert.Equal(17m, transaction.ReservedAfterCredits);
        Assert.Equal("preserve me", transaction.Note);
        account.BalanceCredits -= 0.2m;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(2147483646.8m, (await db.AiCreditAccounts.SingleAsync()).BalanceCredits);
        Assert.NotEqual(fingerprint, await CreditLedgerDeploymentCheck.FingerprintAsync(connection));
        Assert.Throws<NotSupportedException>(() => new Glosify.Migrations.FractionalCredits().DownOperations);
    });

    private static CreditPricingResolver Pricing() => new(Options.Create(new CreditPricingOptions()), Options.Create(new AiUsageOptions()), Options.Create(new RealtimeTranslationOptions()));
    private static AiCreditService Service(GlosifyContext context, Factory factory, TimeProvider? clock = null) => new(context, factory,
        Options.Create(new AiUsageOptions { TrialGrantCredits = 0, MonthlyBudget = new() { Enabled = false } }), Pricing(), new NoTrial(), clock);
    private sealed class NoTrial : ITrialEligibilityService
    {
        public Task<bool> IsEligibleAsync(string userId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
    private sealed class Factory(string connection) : IDbContextFactory<GlosifyContext>
    {
        public GlosifyContext CreateDbContext() => new(new DbContextOptionsBuilder<GlosifyContext>().UseSqlServer(connection).Options);
    }
}
