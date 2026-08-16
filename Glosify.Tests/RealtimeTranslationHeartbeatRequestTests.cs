using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Glosify.Models.Api;
using Xunit;

namespace Glosify.Tests;

public sealed class RealtimeTranslationHeartbeatRequestTests
{
    [Fact]
    public void DiagnosticsRemainOptionalForOlderExtensionVersions()
    {
        var request = new RealtimeTranslationHeartbeatRequest();

        Assert.False(request.WorkerRecovered);
        Assert.Null(request.DroppedAudioMilliseconds);
        Assert.Null(request.BackpressureEvents);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(60_000, 100, true)]
    [InlineData(-1, 0, false)]
    [InlineData(60_001, 0, false)]
    [InlineData(0, 101, false)]
    public void DiagnosticsAreOptionalAndBounded(
        double droppedAudioMilliseconds,
        int backpressureEvents,
        bool expectedValid)
    {
        var parameters = typeof(RealtimeTranslationHeartbeatRequest)
            .GetConstructors().Single().GetParameters();
        var droppedRange = parameters.Single(parameter =>
                parameter.Name == nameof(RealtimeTranslationHeartbeatRequest.DroppedAudioMilliseconds))
            .GetCustomAttribute<RangeAttribute>()!;
        var backpressureRange = parameters.Single(parameter =>
                parameter.Name == nameof(RealtimeTranslationHeartbeatRequest.BackpressureEvents))
            .GetCustomAttribute<RangeAttribute>()!;

        Assert.Equal(expectedValid,
            droppedRange.IsValid(droppedAudioMilliseconds)
            && backpressureRange.IsValid(backpressureEvents));
    }
}
