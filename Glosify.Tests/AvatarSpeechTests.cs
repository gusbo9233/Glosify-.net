using System.Threading.Channels;
using Glosify.Services.Avatar;
using Xunit;

namespace Glosify.Tests;

public sealed class AvatarSpeechTests
{
    [Theory]
    [InlineData("push-to-talk", 1)]
    [InlineData("hands-free", 0)]
    public async Task FullQueueDrainsAndManualCommitFollowsEveryBilledFrame(string mode, int commits)
    {
        var channel = Channel.CreateBounded<byte[]>(32);
        for (var i = 0; i < 32; i++) Assert.True(channel.Writer.TryWrite(Enumerable.Repeat((byte)i, 6400).ToArray()));
        Assert.False(channel.Writer.TryWrite([])); // A sentinel would be lost here.
        channel.Writer.Complete();
        var billed = new List<decimal>();
        var frames = new List<byte[]>();
        var commitCount = 0;
        await AvatarSpeech.SendAudioAsync(channel.Reader, mode,
            (seconds, _) => { billed.Add(seconds); return Task.CompletedTask; },
            (chunk, commit, _) =>
            {
                if (commit) { Assert.Empty(chunk); Assert.Equal(32, frames.Count); commitCount++; }
                else
                {
                    Assert.Equal(0, commitCount);
                    Assert.Equal((frames.Count + 1) / 5m, billed.Last()); // Usage is durable before send.
                    Assert.All(chunk, value => Assert.Equal((byte)frames.Count, value));
                    frames.Add(chunk);
                }
                return Task.CompletedTask;
            }, CancellationToken.None);
        Assert.Equal(32, frames.Count);
        Assert.Equal(Enumerable.Range(1, 32).Select(x => x / 5m), billed);
        Assert.Equal(commits, commitCount);
    }

    [Fact]
    public async Task InterruptedAudioDoesNotCommit()
    {
        var channel = Channel.CreateUnbounded<byte[]>();
        channel.Writer.TryWrite(new byte[6400]); channel.Writer.Complete();
        using var cancellation = new CancellationTokenSource();
        var frames = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AvatarSpeech.SendAudioAsync(channel.Reader, "push-to-talk",
            (_, _) => Task.CompletedTask,
            (_, commit, _) => { Assert.False(commit); frames++; cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token));
        Assert.Equal(1, frames);
    }
}
