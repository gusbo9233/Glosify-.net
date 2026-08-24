using System.Net.WebSockets;
using System.Text;
using Glosify.Controllers.Api;
using Glosify.Services.Ai.Generation;
using Glosify.Services.RealtimeTranslation;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class OpenAiTranslationRelayTests
{
    private static readonly DateTimeOffset TestNow =
        new(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Protocol_UsesDedicatedTranslationEndpointAndLanguageConfiguration()
    {
        var uri = OpenAiTranslationProtocol.BuildWebSocketUri();
        var update = Encoding.UTF8.GetString(
            OpenAiTranslationProtocol.CreateSessionUpdate("es", "safe-learner"));

        Assert.Equal(
            "wss://api.openai.com/v1/realtime/translations?model=gpt-realtime-translate",
            uri.ToString());
        Assert.Equal(
            "{\"type\":\"session.update\",\"session\":{\"safety_identifier\":\"safe-learner\",\"audio\":{\"output\":{\"language\":\"es\"}}}}",
            update);
    }

    [Fact]
    public void Protocol_UsesBearerKeyAndHashedLearnerSafetyIdentifier()
    {
        var headers = OpenAiTranslationProtocol.CreateRequestHeaders(
            "  secret-test-key  ",
            "learner-7");

        Assert.Equal("Bearer secret-test-key", headers.Authorization);
        Assert.Equal(64, headers.SafetyIdentifier.Length);
        Assert.DoesNotContain("learner-7", headers.SafetyIdentifier, StringComparison.Ordinal);
        Assert.Equal(
            OpenAiRequestFactory.CreateSafetyIdentifier("learner-7"),
            headers.SafetyIdentifier);
    }

    [Fact]
    public void Protocol_SendsSessionCloseForGracefulShutdown()
    {
        Assert.Equal(
            "{\"type\":\"session.close\"}",
            Encoding.UTF8.GetString(OpenAiTranslationProtocol.CreateSessionClose()));
    }

    [Fact]
    public void Protocol_AcceptsOnlyBoundedInputAudioMessages()
    {
        Assert.True(OpenAiTranslationProtocol.IsAllowedBrowserMessage(
            "{\"type\":\"session.input_audio_buffer.append\",\"audio\":\"AQIDBA==\"}"u8));
        Assert.False(OpenAiTranslationProtocol.IsAllowedBrowserMessage(
            "{\"type\":\"session.update\",\"session\":{}}"u8));
        Assert.False(OpenAiTranslationProtocol.IsAllowedBrowserMessage(
            "{\"type\":\"session.input_audio_buffer.append\",\"audio\":\"\"}"u8));
    }

    [Fact]
    public void Protocol_DropsAudioOutputButForwardsTranslationText()
    {
        Assert.True(OpenAiTranslationProtocol.ShouldForwardOpenAiMessage(
            "{\"type\":\"response.text.delta\",\"text\":\"Hola\"}"u8));
        Assert.False(OpenAiTranslationProtocol.ShouldForwardOpenAiMessage(
            "{\"type\":\"response.output_audio.delta\",\"delta\":\"AQID\"}"u8));
        Assert.False(OpenAiTranslationProtocol.ShouldForwardOpenAiMessage(
            "{\"type\":\"session.output_audio.delta\",\"delta\":\"AQID\"}"u8));
        Assert.False(OpenAiTranslationProtocol.ShouldForwardOpenAiMessage(
            "{\"type\":\"session.input_transcript.delta\",\"delta\":\"Hello\"}"u8));
        Assert.False(OpenAiTranslationProtocol.ShouldForwardOpenAiMessage(
            "{\"type\":\"conversation.item.input_audio_transcription.completed\",\"transcript\":\"Hello\"}"u8));
    }

    [Fact]
    public void TranscriptAccumulator_PersistsOnlyFinalTranslatedText()
    {
        var accumulator = new OpenAiTranslationTranscriptAccumulator();
        var now = TestNow;

        Assert.Null(accumulator.Apply(
            "{\"type\":\"response.text.delta\",\"response_id\":\"r1\",\"delta\":\"Hola \"}"u8,
            now));
        Assert.Null(accumulator.Apply(
            "{\"type\":\"session.input_transcript.done\",\"transcript\":\"Hello\"}"u8,
            now));
        var completed = accumulator.Apply(
            "{\"type\":\"response.text.done\",\"response_id\":\"r1\",\"text\":\"Hola mundo\"}"u8,
            now);

        Assert.NotNull(completed);
        Assert.Equal("Hola mundo", completed.Text);
        Assert.Equal(1, completed.Sequence);
        Assert.Equal(RealtimeTranslationTranscriptStreams.Translation, completed.Stream);
        Assert.Null(accumulator.Apply(
            "{\"type\":\"response.output_text.done\",\"response_id\":\"r1\",\"text\":\"Hola mundo\"}"u8,
            now));
    }

    [Fact]
    public void TranscriptAccumulator_StoresDeltaOnlyCaptionsOnceTheyGoQuiet()
    {
        var accumulator = new OpenAiTranslationTranscriptAccumulator();
        var start = TestNow;

        // A caption that only ever arrives as deltas, with no id fields to group on.
        Assert.Null(accumulator.Apply(
            "{\"type\":\"session.output_transcript.delta\",\"delta\":\"Dzień \"}"u8,
            start));
        Assert.Null(accumulator.Apply(
            "{\"type\":\"session.output_transcript.delta\",\"delta\":\"dobry\"}"u8,
            start));
        Assert.Empty(accumulator.FlushIdle(start));

        var flushed = Assert.Single(
            accumulator.FlushIdle(start + OpenAiTranslationTranscriptAccumulator.IdleFlush));
        Assert.Equal("Dzień dobry", flushed.Text);
        Assert.Equal(RealtimeTranslationTranscriptStreams.Translation, flushed.Stream);

        // A later caption reuses the same grouping key, so its stored key must
        // still be distinct or the write would treat it as a duplicate.
        Assert.Null(accumulator.Apply(
            "{\"type\":\"session.output_transcript.delta\",\"delta\":\"Do widzenia\"}"u8,
            start + TimeSpan.FromSeconds(30)));
        var second = Assert.Single(accumulator.FlushAll(start + TimeSpan.FromSeconds(31)));
        Assert.Equal("Do widzenia", second.Text);
        Assert.NotEqual(flushed.ProviderEventKey, second.ProviderEventKey);
        Assert.Equal(2, second.Sequence);
    }

    [Fact]
    public void TranscriptAccumulator_RecordsEventTypesWithoutCaptionText()
    {
        var accumulator = new OpenAiTranslationTranscriptAccumulator();
        var now = TestNow;

        accumulator.Apply(
            "{\"type\":\"session.output_transcript.delta\",\"delta\":\"Dzień dobry\"}"u8,
            now);
        accumulator.Apply(
            "{\"type\":\"response.text.done\",\"response_id\":\"r1\",\"text\":\"Good morning\"}"u8,
            now);
        accumulator.Apply(
            "{\"type\":\"session.input_transcript.done\",\"transcript\":\"ignored\"}"u8,
            now);

        Assert.Equal(
            ["response.text.done[response_id]", "session.output_transcript.delta[none]"],
            accumulator.ObservedEventTypes.OrderBy(type => type));
        Assert.DoesNotContain(
            accumulator.ObservedEventTypes,
            type => type.Contains("dobry") || type.Contains("morning"));
    }

    [Fact]
    public void RelayToken_IsSingleUseAndBoundToSession()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var clock = new ManualTimeProvider(TestNow);
        var store = CreateTokenStore(cache, clock);
        var sessionId = Guid.NewGuid();
        var grant = store.Create(
            sessionId,
            "user-1",
            "es",
            translationMode: RealtimeTranslationModes.Enhanced,
            speechProvider: RealtimeSpeechProviders.OpenAi,
            sourceLanguage: "de",
            saveTranscript: true,
            transcriptSourceLanguage: "Polish");

        Assert.True(store.TryRedeem(sessionId, grant.Token, out var authorization));
        Assert.Equal("user-1", authorization.UserId);
        Assert.Equal("es", authorization.TargetLanguage);
        Assert.Equal(RealtimeTranslationModes.Enhanced, authorization.TranslationMode);
        Assert.True(authorization.SaveTranscript);
        Assert.Equal("de", authorization.SourceLanguage);
        Assert.Equal("pl", authorization.TranscriptSourceLanguage);
        Assert.False(store.TryRedeem(sessionId, grant.Token, out _));
    }

    [Fact]
    public void RelayToken_RejectsRemovedEconomicalMode()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = CreateTokenStore(cache, new ManualTimeProvider(TestNow));
        var sessionId = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => store.Create(
            sessionId, "user-1", "sv", RealtimeTranslationModes.Economical,
            RealtimeSpeechProviders.Azure, "auto", false, null));
    }

    [Fact]
    public void RelayToken_BindsScribeModeToElevenLabsProvider()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = CreateTokenStore(cache, new ManualTimeProvider(TestNow));
        var sessionId = Guid.NewGuid();
        var grant = store.Create(
            sessionId,
            "user-1",
            "sv",
            RealtimeTranslationModes.Scribe,
            RealtimeSpeechProviders.ElevenLabs,
            "auto",
            saveTranscript: false,
            transcriptSourceLanguage: null);

        Assert.True(store.TryRedeem(sessionId, grant.Token, out var authorization));
        Assert.Equal(RealtimeTranslationModes.Scribe, authorization.TranslationMode);
        Assert.Equal(RealtimeSpeechProviders.ElevenLabs, authorization.SpeechProvider);
        Assert.Equal("auto", authorization.SourceLanguage);

        Assert.Throws<ArgumentException>(() => store.Create(
            Guid.NewGuid(),
            "user-1",
            "sv",
            RealtimeTranslationModes.Scribe,
            RealtimeSpeechProviders.Azure,
            "pl",
            saveTranscript: false,
            transcriptSourceLanguage: null));
    }

    [Fact]
    public void RelayToken_WrongSessionConsumesGrantAndExpiredGrantFails()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var clock = new ManualTimeProvider(TestNow);
        var store = CreateTokenStore(cache, clock);
        var sessionId = Guid.NewGuid();
        var wrongSessionGrant = store.Create(
            sessionId,
            "user-1",
            "es",
            translationMode: RealtimeTranslationModes.Enhanced,
            speechProvider: RealtimeSpeechProviders.OpenAi,
            sourceLanguage: null,
            saveTranscript: false,
            transcriptSourceLanguage: null);

        Assert.False(store.TryRedeem(Guid.NewGuid(), wrongSessionGrant.Token, out _));
        Assert.False(store.TryRedeem(sessionId, wrongSessionGrant.Token, out _));

        var expiredGrant = store.Create(
            sessionId,
            "user-1",
            "es",
            translationMode: RealtimeTranslationModes.Enhanced,
            speechProvider: RealtimeSpeechProviders.OpenAi,
            sourceLanguage: null,
            saveTranscript: false,
            transcriptSourceLanguage: null);
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.False(store.TryRedeem(sessionId, expiredGrant.Token, out _));
    }

    [Fact]
    public void RelayToken_RequiresSupportedSourceLanguageWhenSaving()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = CreateTokenStore(cache, new ManualTimeProvider(TestNow));

        Assert.Throws<ArgumentException>(() => store.Create(
            Guid.NewGuid(),
            "user-1",
            "es",
            translationMode: RealtimeTranslationModes.Enhanced,
            speechProvider: RealtimeSpeechProviders.OpenAi,
            sourceLanguage: null,
            saveTranscript: true,
            transcriptSourceLanguage: "tlh"));
    }

    [Fact]
    public void RelayController_RequiresExactlyOneValidTokenSubprotocol()
    {
        var token = new string('A', 43);
        Assert.Equal(token, RealtimeTranslationRelayController.ReadRelayToken(
            ["glosify-realtime", "relay-token." + token]));
        Assert.Null(RealtimeTranslationRelayController.ReadRelayToken(
            ["relay-token." + token, "relay-token." + new string('B', 43)]));
        Assert.Null(RealtimeTranslationRelayController.ReadRelayToken(
            ["glosify-realtime", "relay-token.invalid"]));
    }

    [Theory]
    [InlineData(RealtimeTranslationModes.Enhanced, 1, 0)]
    [InlineData(RealtimeTranslationModes.Scribe, 0, 1)]
    public async Task RelayRouter_DelegatesToTheAuthorizedMode(
        string mode,
        int expectedEnhancedCalls,
        int expectedScribeCalls)
    {
        var enhanced = new RecordingEnhancedRelay();
        var scribe = new RecordingScribeRelay();
        var router = new RealtimeTranslationRelayRouter(enhanced, scribe);
        using var socket = new ClientWebSocket();
        var authorization = new RealtimeTranslationRelayAuthorization(
            Guid.NewGuid(),
            "user-1",
            "sv",
            mode,
            mode switch
            {
                RealtimeTranslationModes.Scribe => RealtimeSpeechProviders.ElevenLabs,
                _ => RealtimeSpeechProviders.OpenAi,
            },
            mode == RealtimeTranslationModes.Scribe ? "pl" : null,
            SaveTranscript: false,
            TranscriptSourceLanguage: null);

        await router.RelayAsync(socket, authorization);

        Assert.Equal(expectedEnhancedCalls, enhanced.Calls);
        Assert.Equal(expectedScribeCalls, scribe.Calls);
    }

    [Fact]
    public async Task RelayRouter_RejectsRemovedEconomicalMode()
    {
        var router = new RealtimeTranslationRelayRouter(
            new RecordingEnhancedRelay(),
            new RecordingScribeRelay());
        using var socket = new ClientWebSocket();
        var authorization = new RealtimeTranslationRelayAuthorization(
            Guid.NewGuid(), "user-1", "sv", RealtimeTranslationModes.Economical,
            RealtimeSpeechProviders.Azure, "auto", false, null);

        await Assert.ThrowsAsync<RealtimeTranslationValidationException>(
            () => router.RelayAsync(socket, authorization));
    }

    [Fact]
    public async Task RelayRouter_RejectsUnknownMode()
    {
        var router = new RealtimeTranslationRelayRouter(
            new RecordingEnhancedRelay(),
            new RecordingScribeRelay());
        using var socket = new ClientWebSocket();
        var authorization = new RealtimeTranslationRelayAuthorization(
            Guid.NewGuid(),
            "user-1",
            "sv",
            "unknown",
            RealtimeSpeechProviders.OpenAi,
            SourceLanguage: null,
            SaveTranscript: false,
            TranscriptSourceLanguage: null);

        await Assert.ThrowsAsync<RealtimeTranslationValidationException>(
            () => router.RelayAsync(socket, authorization));
    }

    [Fact]
    public async Task ScribePipeline_PropagatesProducerFailureWhenConsumerCompletesFirst()
    {
        var producerFailure = new RealtimeTranslationUpstreamException(
            "ElevenLabs Scribe v2 ended the transcription stream.");

        var exception = await Assert.ThrowsAsync<RealtimeTranslationUpstreamException>(() =>
            OpenAiTranslationRelay.AwaitScribePipelineAsync(
                Task.FromException(producerFailure),
                Task.CompletedTask));

        Assert.Same(producerFailure, exception);
    }

    private static RealtimeTranslationRelayTokenStore CreateTokenStore(
        IMemoryCache cache,
        TimeProvider timeProvider) =>
        new(
            cache,
            Options.Create(new RealtimeTranslationOptions { RelayTokenLifetimeSeconds = 120 }),
            timeProvider);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class RecordingEnhancedRelay : IEnhancedTranslationRelay
    {
        public int Calls { get; private set; }

        public Task RelayAsync(
            WebSocket browserSocket,
            RealtimeTranslationRelayAuthorization authorization,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingScribeRelay : IScribeTranslationRelay
    {
        public int Calls { get; private set; }

        public Task RelayAsync(
            WebSocket browserSocket,
            RealtimeTranslationRelayAuthorization authorization,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }
}
