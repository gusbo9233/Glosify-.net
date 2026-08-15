using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Glosify.Services.RealtimeTranslation;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Glosify.Tests;

public sealed class ElevenLabsRealtimeSpeechTranscriberTests
{
    [Fact]
    public async Task Transcribe_SendsPcmAndEmitsThrottledPartialThenTimestampedCommit()
    {
        var socket = new ScriptedWebSocket("pl");
        var factory = new RecordingFactory(socket);
        var transcriber = CreateTranscriber(factory);
        var audio = Channel.CreateUnbounded<byte[]>();
        var output = Channel.CreateUnbounded<RecognizedSpeechSegment>();
        await audio.Writer.WriteAsync([1, 2, 3, 4]);
        audio.Writer.Complete();

        await transcriber.TranscribeAsync(
            RealtimeSpeechProviders.ElevenLabs,
            "pl",
            audio.Reader,
            output.Writer,
            emitPartials: true,
            cancellationToken: CancellationToken.None);

        Assert.Equal("test-key", factory.ApiKey);
        Assert.Contains("model_id=scribe_v2_realtime", factory.Endpoint!.Query);
        Assert.Contains("audio_format=pcm_24000", factory.Endpoint.Query);
        Assert.Contains("commit_strategy=vad", factory.Endpoint.Query);
        Assert.Contains("language_code=pl", factory.Endpoint.Query);
        Assert.Contains("enable_logging=true", factory.Endpoint.Query);
        var messages = socket.Sent.Select(Parse).ToArray();
        Assert.Equal("input_audio_chunk", messages[0].GetProperty("message_type").GetString());
        Assert.Equal(24_000, messages[0].GetProperty("sample_rate").GetInt32());
        Assert.Equal(Convert.ToBase64String([1, 2, 3, 4]),
            messages[0].GetProperty("audio_base_64").GetString());
        Assert.Equal("input_audio_chunk", messages[1].GetProperty("message_type").GetString());
        Assert.True(messages[1].GetProperty("commit").GetBoolean());

        var partial = await output.Reader.ReadAsync();
        Assert.Equal(1, partial.Sequence);
        Assert.Equal("Dzień", partial.Text);
        Assert.False(partial.IsFinal);

        var segment = await output.Reader.ReadAsync();
        Assert.Equal(1, segment.Sequence);
        Assert.Equal("Dzień dobry", segment.Text);
        Assert.Equal("pl", segment.SourceLanguage);
        Assert.Equal("pl-PL", segment.SourceLocale);
        Assert.False(segment.IsAutoDetected);
        Assert.True(segment.IsFinal);
        Assert.False(output.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Transcribe_EmitsRevisedPartialAtThrottleInterval()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var socket = new ScriptedWebSocket(
            "pl",
            beforeSecondPartial: () => clock.Advance(TimeSpan.FromMilliseconds(750)));
        var transcriber = CreateTranscriber(new RecordingFactory(socket), clock);
        var audio = Channel.CreateUnbounded<byte[]>();
        var output = Channel.CreateUnbounded<RecognizedSpeechSegment>();
        await audio.Writer.WriteAsync([1, 2]);
        audio.Writer.Complete();

        await transcriber.TranscribeAsync(
            RealtimeSpeechProviders.ElevenLabs,
            "pl",
            audio.Reader,
            output.Writer,
            emitPartials: true,
            cancellationToken: CancellationToken.None);

        var initialPartial = await output.Reader.ReadAsync();
        Assert.Equal("Dzień", initialPartial.Text);
        Assert.False(initialPartial.IsFinal);
        var revisedPartial = await output.Reader.ReadAsync();
        Assert.Equal("Dzień dob", revisedPartial.Text);
        Assert.False(revisedPartial.IsFinal);
        Assert.Equal(initialPartial.Sequence, revisedPartial.Sequence);
        var finalSegment = await output.Reader.ReadAsync();
        Assert.Equal("Dzień dobry", finalSegment.Text);
        Assert.Equal(initialPartial.Sequence, finalSegment.Sequence);
        Assert.True(finalSegment.IsFinal);
        Assert.False(output.Reader.TryRead(out _));
    }

    [Fact]
    public void AutoDetection_AcceptsProviderLanguagesWithoutConstrainingTheEndpoint()
    {
        var transcriber = CreateTranscriber(new RecordingFactory(new ScriptedWebSocket("pl")));

        Assert.Equal("pl", transcriber.ResolveSourceLanguage("auto", "pl").Code);
        Assert.Equal("fr", transcriber.ResolveSourceLanguage("auto", "fr").Code);
        var endpoint = transcriber.BuildEndpoint("auto");
        Assert.DoesNotContain("language_code=", endpoint.Query);
        Assert.DoesNotContain("secondary_languages=", endpoint.Query);
    }

    [Fact]
    public void CatalogLanguageHint_UsesScribeIsoCodeAndCanonicalLocale()
    {
        var transcriber = CreateTranscriber(new RecordingFactory(new ScriptedWebSocket("srp")));

        var endpoint = transcriber.BuildEndpoint("sr");
        var resolved = transcriber.ResolveSourceLanguage("sr", null);

        Assert.Contains("language_code=srp", endpoint.Query);
        Assert.Equal("sr-Latn", resolved.Code);
        Assert.Equal("sr-Latn-RS", resolved.Locale);
        Assert.Equal("srp", resolved.ScribeCode);
    }

    [Fact]
    public async Task AutoDetection_MarksDetectedLanguageForTranslatorAutoDetection()
    {
        var socket = new ScriptedWebSocket("fr");
        var transcriber = CreateTranscriber(new RecordingFactory(socket));
        var audio = Channel.CreateUnbounded<byte[]>();
        var output = Channel.CreateUnbounded<RecognizedSpeechSegment>();
        await audio.Writer.WriteAsync([1, 2]);
        audio.Writer.Complete();

        await transcriber.TranscribeAsync(
            RealtimeSpeechProviders.ElevenLabs,
            "auto",
            audio.Reader,
            output.Writer,
            emitPartials: true,
            cancellationToken: CancellationToken.None);

        _ = await output.Reader.ReadAsync();
        var segment = await output.Reader.ReadAsync();
        Assert.Equal("fr", segment.SourceLanguage);
        Assert.True(segment.IsAutoDetected);
        Assert.True(segment.IsFinal);
    }

    [Fact]
    public async Task FinalOnlyMode_IgnoresPartialsForEnhancedTranscriptCapture()
    {
        var socket = new ScriptedWebSocket("pl");
        var transcriber = CreateTranscriber(new RecordingFactory(socket));
        var audio = Channel.CreateUnbounded<byte[]>();
        var output = Channel.CreateUnbounded<RecognizedSpeechSegment>();
        await audio.Writer.WriteAsync([1, 2]);
        audio.Writer.Complete();

        await transcriber.TranscribeAsync(
            RealtimeSpeechProviders.ElevenLabs,
            "pl",
            audio.Reader,
            output.Writer,
            emitPartials: false,
            cancellationToken: CancellationToken.None);

        var segment = await output.Reader.ReadAsync();
        Assert.Equal("Dzień dobry", segment.Text);
        Assert.True(segment.IsFinal);
        Assert.False(output.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Transcribe_SanitizesProviderErrors()
    {
        var socket = new ScriptedWebSocket("pl", providerError: true);
        var transcriber = CreateTranscriber(new RecordingFactory(socket));
        var audio = Channel.CreateUnbounded<byte[]>();
        var output = Channel.CreateUnbounded<RecognizedSpeechSegment>();
        await audio.Writer.WriteAsync([1, 2]);
        audio.Writer.Complete();

        var exception = await Assert.ThrowsAsync<RealtimeTranslationUpstreamException>(() =>
            transcriber.TranscribeAsync(
                RealtimeSpeechProviders.ElevenLabs,
                "pl",
                audio.Reader,
                output.Writer,
                emitPartials: true,
                cancellationToken: CancellationToken.None));

        Assert.DoesNotContain("secret-provider-detail", exception.Message);
        Assert.Contains("ElevenLabs Scribe v2", exception.Message);
    }

    [Fact]
    public async Task Transcribe_CancellationCompletesWithoutEmittingText()
    {
        var socket = new ScriptedWebSocket("pl");
        var transcriber = CreateTranscriber(new RecordingFactory(socket));
        var audio = Channel.CreateUnbounded<byte[]>();
        var output = Channel.CreateUnbounded<RecognizedSpeechSegment>();
        using var cancellation = new CancellationTokenSource();

        var task = transcriber.TranscribeAsync(
            RealtimeSpeechProviders.ElevenLabs,
            "pl",
            audio.Reader,
            output.Writer,
            emitPartials: true,
            cancellationToken: cancellation.Token);
        cancellation.Cancel();

        await task;
        Assert.True(output.Reader.Completion.IsCompletedSuccessfully);
        Assert.False(output.Reader.TryRead(out _));
        Assert.False(socket.DisposeWhileReceiving);
    }

    private static ElevenLabsRealtimeSpeechTranscriber CreateTranscriber(
        IElevenLabsRealtimeWebSocketFactory factory,
        TimeProvider? timeProvider = null) =>
        new(
            factory,
            Options.Create(new RealtimeTranslationOptions
            {
                ElevenLabs = new ElevenLabsRealtimeSpeechOptions
                {
                    Enabled = true,
                    ApiKey = "test-key",
                },
                SourceLanguages =
                [
                    new RealtimeTranslationSourceLanguageOptions
                    {
                        Code = "pl",
                        Name = "Polish",
                        Locale = "pl-PL",
                        TranslatorCode = "pl",
                        ScribeCode = "pl",
                        AutoDetect = true,
                    },
                ],
            }),
            timeProvider ?? new FakeTimeProvider(
                new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero)));

    private static JsonElement Parse(byte[] payload) =>
        JsonDocument.Parse(payload).RootElement.Clone();

    private sealed class RecordingFactory(WebSocket socket) : IElevenLabsRealtimeWebSocketFactory
    {
        public Uri? Endpoint { get; private set; }
        public string? ApiKey { get; private set; }

        public Task<WebSocket> ConnectAsync(
            Uri endpoint,
            string apiKey,
            CancellationToken cancellationToken)
        {
            Endpoint = endpoint;
            ApiKey = apiKey;
            return Task.FromResult(socket);
        }
    }

    private sealed class ScriptedWebSocket(
        string detectedLanguage,
        bool providerError = false,
        Action? beforeSecondPartial = null) : WebSocket
    {
        private readonly Channel<Frame> _received = Channel.CreateUnbounded<Frame>();
        private WebSocketState _state = WebSocketState.Open;
        private int _activeReceives;

        public List<byte[]> Sent { get; } = [];
        public bool DisposeWhileReceiving { get; private set; }
        public override WebSocketCloseStatus? CloseStatus { get; }
        public override string? CloseStatusDescription { get; }
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            DisposeWhileReceiving = Volatile.Read(ref _activeReceives) > 0;
            _state = WebSocketState.Closed;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _activeReceives);
            try
            {
                var frame = await _received.Reader.ReadAsync(cancellationToken);
                frame.BeforeRead?.Invoke();
                if (frame.MessageType == WebSocketMessageType.Close)
                {
                    _state = WebSocketState.CloseReceived;
                    return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
                }
                frame.Payload.AsSpan().CopyTo(buffer.AsSpan());
                return new WebSocketReceiveResult(
                    frame.Payload.Length,
                    frame.MessageType,
                    true);
            }
            finally
            {
                await Task.Yield();
                Interlocked.Decrement(ref _activeReceives);
            }
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            var payload = buffer.ToArray();
            Sent.Add(payload);
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("commit", out var commit)
                && commit.GetBoolean())
            {
                if (providerError)
                {
                    Enqueue(new
                    {
                        message_type = "auth_error",
                        error = "secret-provider-detail",
                    });
                }
                else
                {
                    Enqueue(new { message_type = "partial_transcript", text = "Dzień" });
                    Enqueue(
                        new { message_type = "partial_transcript", text = "Dzień dob" },
                        beforeSecondPartial);
                    Enqueue(new { message_type = "committed_transcript", text = "Dzień dobry" });
                    Enqueue(new
                    {
                        message_type = "committed_transcript_with_timestamps",
                        text = "Dzień dobry",
                        language_code = detectedLanguage,
                        words = Array.Empty<object>(),
                    });
                }
                _received.Writer.TryWrite(new Frame([], WebSocketMessageType.Close));
            }
            return Task.CompletedTask;
        }

        private void Enqueue(object message, Action? beforeRead = null) =>
            _received.Writer.TryWrite(new Frame(
                JsonSerializer.SerializeToUtf8Bytes(message),
                WebSocketMessageType.Text,
                beforeRead));

        private sealed record Frame(
            byte[] Payload,
            WebSocketMessageType MessageType,
            Action? BeforeRead = null);
    }
}
