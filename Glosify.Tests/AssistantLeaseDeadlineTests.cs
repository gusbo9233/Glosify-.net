using Glosify.Services.Ai.Assistant.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantLeaseDeadlineTests
{
    [Fact]
    public async Task Stalled_renewal_cancels_work_before_lease_expiry()
    {
        var clock = new FakeTimeProvider();
        using var step = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<int> Renew(CancellationToken ct)
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 1;
        }
        var heartbeat = AssistantRunWorker.HeartbeatAsync(Guid.NewGuid(), step, Renew,
            TimeSpan.FromSeconds(20), NullLogger.Instance, clock, clock.GetUtcNow());
        try
        {
            clock.Advance(TimeSpan.FromSeconds(20));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(89));
            Assert.False(step.IsCancellationRequested);
            clock.Advance(TimeSpan.FromSeconds(1));
            await heartbeat.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(step.IsCancellationRequested);
        }
        finally { await step.CancelAsync(); await heartbeat; }
    }

    [Fact]
    public async Task Slow_success_renews_deadline_from_request_start_not_completion()
    {
        var clock = new FakeTimeProvider();
        using var step = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextRenewal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<int> Renew(CancellationToken ct)
        {
            if (++calls == 1)
            {
                entered.SetResult();
                return await release.Task.WaitAsync(ct);
            }
            nextRenewal.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 1;
        }
        var heartbeat = AssistantRunWorker.HeartbeatAsync(Guid.NewGuid(), step, Renew,
            TimeSpan.FromSeconds(20), NullLogger.Instance, clock, clock.GetUtcNow());
        try
        {
            clock.Advance(TimeSpan.FromSeconds(20));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(60));
            release.SetResult(1);
            await nextRenewal.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(49));
            Assert.False(step.IsCancellationRequested);
            clock.Advance(TimeSpan.FromSeconds(1));
            await heartbeat.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(step.IsCancellationRequested);
        }
        finally { await step.CancelAsync(); await heartbeat; }
    }

    [Fact]
    public async Task Slow_claim_cancels_before_starting_a_renewal()
    {
        var clock = new FakeTimeProvider();
        var started = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromMinutes(2));
        using var step = new CancellationTokenSource();
        await AssistantRunWorker.HeartbeatAsync(Guid.NewGuid(), step,
            _ => throw new InvalidOperationException("Expired claims must not renew"),
            TimeSpan.FromSeconds(20), NullLogger.Instance, clock, started);
        Assert.True(step.IsCancellationRequested);
    }
}
