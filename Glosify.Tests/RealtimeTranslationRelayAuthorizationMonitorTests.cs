using Glosify.Services.RealtimeTranslation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Glosify.Tests;

public sealed class RealtimeTranslationRelayAuthorizationMonitorTests
{
    [Fact]
    public async Task AudioCapacityWaitsUntilTheMinuteIsCharged()
    {
        var startedAt = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(startedAt);
        var billing = new RealtimeTranslationRelayBillingState(chargedMinutes: 0);
        var monitor = new RealtimeTranslationRelayAuthorizationMonitor(
            scopeFactory: null!,
            Options.Create(new RealtimeTranslationOptions()),
            clock,
            NullLogger<RealtimeTranslationRelayAuthorizationMonitor>.Instance);

        var waiting = monitor.WaitForAudioCapacityAsync(
            requestedBytes: 1,
            startedAt,
            billing,
            CancellationToken.None);
        await Task.Yield();
        Assert.False(waiting.IsCompleted);

        Volatile.Write(ref billing.ChargedMinutes, 1);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await waiting.WaitAsync(TimeSpan.FromSeconds(1));
    }
}
