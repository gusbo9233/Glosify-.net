using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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

    [Theory]
    [InlineData("close")]
    [InlineData("insufficient_audio_activity")]
    [InlineData("error")]
    [InlineData("send")]
    public async Task PreparedSessionFailureBeforeATranscriptReplaysTheTurnOnAFreshSession(string failure)
    {
        var prepared = new FakeSocket();
        var fresh = new FakeSocket { Reply = sent => sent.GetProperty("commit").GetBoolean()
            ? new { message_type = "committed_transcript", text = " Hej! " } : null };
        var audio = Channel.CreateUnbounded<byte[]>();
        var connects = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var text = await AvatarSpeech.RecognizeAsync(prepared, AvatarWire.ReadAsync(prepared, CancellationToken.None),
            _ => { connects++; return Task.FromResult<WebSocket>(fresh); }, audio.Reader, "push-to-talk",
            (_, _) => Task.CompletedTask, _ => Task.CompletedTask, () =>
            {
                // The provider ends the idle session just after it passed the pre-turn check.
                if (failure == "send") prepared.FailSends = true;
                else if (failure == "error") prepared.Receive(new { message_type = "error" });
                else
                {
                    if (failure != "close") prepared.Receive(new { message_type = failure });
                    prepared.CloseFromProvider();
                }
                audio.Writer.TryWrite([1, 2]); audio.Writer.TryWrite([3, 4]); audio.Writer.Complete();
                return Task.CompletedTask;
            }, timeout.Token);
        Assert.Equal("Hej!", text);
        Assert.Equal(1, connects);
        Assert.True(prepared.Aborted);
        Assert.Equal(["AQI=", "AwQ=", ""], fresh.Sent.Select(x => x.GetProperty("audio_base_64").GetString()));
        Assert.Equal([false, false, true], fresh.Sent.Select(x => x.GetProperty("commit").GetBoolean()));
    }

    [Fact]
    public async Task PreparedSessionThatAlreadyTranscribedIsNotReplayed()
    {
        var prepared = new FakeSocket();
        var audio = Channel.CreateUnbounded<byte[]>();
        var connects = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<WebSocketException>(() => AvatarSpeech.RecognizeAsync(prepared,
            AvatarWire.ReadAsync(prepared, CancellationToken.None),
            _ => { connects++; return Task.FromResult<WebSocket>(new FakeSocket()); }, audio.Reader, "hands-free",
            (_, _) => Task.CompletedTask, _ => { prepared.CloseFromProvider(); return Task.CompletedTask; }, () =>
            {
                audio.Writer.TryWrite([1, 2]);
                prepared.Receive(new { message_type = "partial_transcript", text = "He" });
                return Task.CompletedTask;
            }, timeout.Token));
        Assert.Equal(0, connects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshSessionFailureIsReportedWithoutAnotherRetry(bool prepare)
    {
        var prepared = prepare ? new FakeSocket() : null;
        var fresh = new FakeSocket();
        var audio = Channel.CreateUnbounded<byte[]>();
        var connects = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<Exception>(() => AvatarSpeech.RecognizeAsync(prepared,
            prepared is null ? null : AvatarWire.ReadAsync(prepared, CancellationToken.None),
            _ => { connects++; return Task.FromResult<WebSocket>(fresh); }, audio.Reader, "push-to-talk",
            (_, _) => Task.CompletedTask, _ => Task.CompletedTask, () =>
            {
                prepared?.CloseFromProvider();
                fresh.Receive(new { message_type = "error" });
                audio.Writer.TryWrite([1, 2]); audio.Writer.Complete();
                return Task.CompletedTask;
            }, timeout.Token));
        Assert.Equal(1, connects);
        Assert.True(fresh.Aborted);
    }

    private sealed class FakeSocket : WebSocket
    {
        private readonly Channel<string?> incoming = Channel.CreateUnbounded<string?>();
        private WebSocketState state = WebSocketState.Open;
        public ConcurrentQueue<JsonElement> Sent { get; } = new();
        public Func<JsonElement, object?>? Reply;
        public bool FailSends;
        public bool Aborted => state == WebSocketState.Aborted;
        public void Receive(object message) => incoming.Writer.TryWrite(JsonSerializer.Serialize(message));
        public void CloseFromProvider() => incoming.Writer.TryWrite(null);
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;
        public override void Abort() { state = WebSocketState.Aborted; incoming.Writer.TryComplete(); }
        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            string? text;
            try { text = await incoming.Reader.ReadAsync(cancellationToken); }
            catch (ChannelClosedException) { throw new WebSocketException("Aborted."); }
            if (text is null) return new(0, WebSocketMessageType.Close, true);
            return new(Encoding.UTF8.GetBytes(text.AsSpan(), buffer.AsSpan()), WebSocketMessageType.Text, true);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            if (state != WebSocketState.Open || FailSends) throw new WebSocketException("Send failed.");
            using var document = JsonDocument.Parse(buffer);
            var sent = document.RootElement.Clone();
            Sent.Enqueue(sent);
            if (Reply?.Invoke(sent) is { } reply) Receive(reply);
            return Task.CompletedTask;
        }
    }
}
