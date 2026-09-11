using Glosify.Data;
using Glosify.Services.Abuse;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class RequestResourceReservationsTests
{
    [Fact]
    public async Task CompletedRequestAcceptsWrappedDatabaseCancellation()
    {
        using var active = new CancellationTokenSource();
        var error = new InvalidOperationException("Execution strategy wrapper", new DbUpdateException("Database operation cancelled"));
        var quotas = Quotas(() => { active.Cancel(); throw error; });
        var reservations = Tracked();

        await reservations.RenewWhileActiveAsync(quotas, active, TimeSpan.FromMilliseconds(1));

        Assert.True(active.IsCancellationRequested);
        Assert.Single(reservations.Pending); // Cleanup still owns the reservation.
    }

    [Fact]
    public async Task ActiveRequestDoesNotHideWrappedDatabaseFailure()
    {
        using var active = new CancellationTokenSource();
        var error = new InvalidOperationException("Execution strategy wrapper", new DbUpdateException("Database unavailable"));
        var quotas = Quotas(() => throw error);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Tracked().RenewWhileActiveAsync(quotas, active, TimeSpan.FromMilliseconds(1)));

        Assert.Same(error, thrown);
        Assert.True(active.IsCancellationRequested);
    }

    [Fact]
    public async Task CompletedRequestDoesNotHideUnrelatedFailure()
    {
        using var active = new CancellationTokenSource();
        var error = new InvalidOperationException("Unrelated failure", new ArgumentException("Invalid configuration"));
        var quotas = Quotas(() => { active.Cancel(); throw error; });

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Tracked().RenewWhileActiveAsync(quotas, active, TimeSpan.FromMilliseconds(1)));

        Assert.Same(error, thrown);
    }

    private static RequestResourceReservations Tracked()
    {
        var reservations = new RequestResourceReservations();
        reservations.Track(Guid.NewGuid(), "owner");
        return reservations;
    }

    private static ResourceQuotaService Quotas(Func<GlosifyContext> create) =>
        new(new Factory(create), Options.Create(new AbuseOptions()));

    private sealed class Factory(Func<GlosifyContext> create) : IDbContextFactory<GlosifyContext>
    {
        public GlosifyContext CreateDbContext() => create();
    }
}
