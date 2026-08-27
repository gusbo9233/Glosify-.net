using Glosify.Infrastructure.Concurrency;
using Xunit;

namespace Glosify.Tests;

public sealed class ReferenceCountedKeyedAsyncLockTests
{
    [Fact]
    public async Task SameKey_IsSerialized_AndEntryIsPruned()
    {
        using var keyedLock = new ReferenceCountedKeyedAsyncLock();
        var first = await keyedLock.AcquireAsync("session:1");
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = AcquireSecondAsync();

        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(entered.Task.IsCompleted);
        Assert.Equal(1, keyedLock.ActiveKeyCount);

        await first.DisposeAsync();
        await second.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(entered.Task.IsCompletedSuccessfully);
        Assert.Equal(0, keyedLock.ActiveKeyCount);

        async Task AcquireSecondAsync()
        {
            var pendingLease = keyedLock.AcquireAsync("session:1");
            waiting.SetResult();
            await using var lease = await pendingLease;
            entered.SetResult();
        }
    }

    [Fact]
    public async Task CancelledWaiter_ReleasesItsReference()
    {
        using var keyedLock = new ReferenceCountedKeyedAsyncLock();
        var holder = await keyedLock.AcquireAsync("session:2");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await keyedLock.AcquireAsync("session:2", cancellation.Token));
        Assert.Equal(1, keyedLock.ActiveKeyCount);

        await holder.DisposeAsync();
        Assert.Equal(0, keyedLock.ActiveKeyCount);
    }

    [Fact]
    public async Task CompletedKeys_DoNotAccumulate()
    {
        using var keyedLock = new ReferenceCountedKeyedAsyncLock();

        for (var index = 0; index < 100; index++)
        {
            await using var lease = await keyedLock.AcquireAsync($"session:{index}");
        }

        Assert.Equal(0, keyedLock.ActiveKeyCount);
    }
}
