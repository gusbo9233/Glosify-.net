using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Assistant;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantTurnLeaseServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero));
    private FactoryBackedGlosifyContext _root = null!;
    private AssistantTurnLeaseService _leases = null!;
    private Guid _threadId;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseSqlite(_connection)
            .Options;
        _root = new FactoryBackedGlosifyContext(options);
        await _root.Database.EnsureCreatedAsync();
        _threadId = Guid.NewGuid();
        _root.Users.Add(new ApplicationUser
        {
            Id = "user-1",
            UserName = "user-1",
            NormalizedUserName = "USER-1",
        });
        _root.AssistantThreads.Add(new AssistantThread
        {
            Id = _threadId,
            UserId = "user-1",
            Title = "Lease test",
            CreatedAt = _clock.GetUtcNow(),
            UpdatedAt = _clock.GetUtcNow(),
        });
        await _root.SaveChangesAsync();
        _leases = new AssistantTurnLeaseService(new TestDbContextFactory(_root), _clock);
    }

    public async Task DisposeAsync()
    {
        await _root.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Acquisition_is_exclusive_and_release_enforces_ownership()
    {
        var owner = await _leases.TryAcquireAsync(_threadId, "user-1", default);

        Assert.NotNull(owner);
        Assert.Null(await _leases.TryAcquireAsync(_threadId, "user-1", default));
        Assert.Null(await _leases.TryAcquireAsync(_threadId, "someone-else", default));

        await _leases.ReleaseAsync(_threadId, Guid.NewGuid(), default);
        Assert.Null(await _leases.TryAcquireAsync(_threadId, "user-1", default));

        await _leases.ReleaseAsync(_threadId, owner.Value, default);
        Assert.NotNull(await _leases.TryAcquireAsync(_threadId, "user-1", default));
    }

    [Fact]
    public async Task Expired_lease_can_be_taken_over_and_old_owner_cannot_touch_successor()
    {
        var oldOwner = Assert.IsType<Guid>(await _leases.TryAcquireAsync(_threadId, "user-1", default));
        _clock.Advance(AssistantTurnLeaseService.LeaseDuration.Add(TimeSpan.FromSeconds(1)));
        var successor = Assert.IsType<Guid>(await _leases.TryAcquireAsync(_threadId, "user-1", default));

        Assert.NotEqual(oldOwner, successor);
        Assert.False(await _leases.RenewAsync(_threadId, oldOwner, default));
        await _leases.ReleaseAsync(_threadId, oldOwner, default);
        Assert.True(await _leases.RenewAsync(_threadId, successor, default));

        await using var verification = new TestDbContextFactory(_root).CreateDbContext();
        var thread = await verification.AssistantThreads.AsNoTracking().SingleAsync(item => item.Id == _threadId);
        Assert.Equal(successor, thread.ActiveTurnId);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.Add(AssistantTurnLeaseService.LeaseDuration), thread.ActiveTurnExpiresAt);
    }

    [Fact]
    public async Task Expired_lease_cannot_be_renewed_without_takeover()
    {
        var owner = Assert.IsType<Guid>(await _leases.TryAcquireAsync(_threadId, "user-1", default));
        var originalExpiry = _clock.GetUtcNow().UtcDateTime.Add(AssistantTurnLeaseService.LeaseDuration);
        _clock.Advance(AssistantTurnLeaseService.LeaseDuration.Add(TimeSpan.FromSeconds(1)));

        Assert.False(await _leases.RenewAsync(_threadId, owner, default));

        await using var verification = new TestDbContextFactory(_root).CreateDbContext();
        var thread = await verification.AssistantThreads.AsNoTracking().SingleAsync(item => item.Id == _threadId);
        Assert.Equal(owner, thread.ActiveTurnId);
        Assert.Equal(originalExpiry, thread.ActiveTurnExpiresAt);
    }
}
