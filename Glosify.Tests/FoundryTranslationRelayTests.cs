using System.Text;
using Glosify.Controllers.Api;
using Glosify.Services.RealtimeTranslation;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class FoundryTranslationRelayTests
{
    [Fact]
    public void Protocol_UsesDedicatedTranslationEndpointAndLanguageConfiguration()
    {
        var options = new RealtimeTranslationOptions
        {
            FoundryEndpoint = "https://glosify-foundry.openai.azure.com/",
            Deployment = "glosify-realtime-translate",
        };

        var uri = FoundryTranslationProtocol.BuildWebSocketUri(options);
        var update = Encoding.UTF8.GetString(
            FoundryTranslationProtocol.CreateSessionUpdate("es"));

        Assert.Equal(
            "wss://glosify-foundry.openai.azure.com/openai/v1/realtime/translations?model=glosify-realtime-translate",
            uri.ToString());
        Assert.Contains("\"type\":\"session.update\"", update);
        Assert.Contains("\"language\":\"es\"", update);
        // Text only: the relay discards synthesised audio anyway, so asking for it just
        // burns bandwidth, CPU and allocations on the single-core instance.
        Assert.Contains("\"output_modalities\":[\"text\"]", update);
    }

    [Fact]
    public void Protocol_UsesWhisperTranscriptionEndpointWithQuizLanguageHint()
    {
        var options = new RealtimeTranslationOptions
        {
            FoundryEndpoint = "https://glosify-foundry.openai.azure.com/",
            SourceTranscriptionDeployment = "gpt-realtime-whisper",
            SourceTranscriptionDelay = "medium",
        };

        var uri = FoundryTranslationProtocol.BuildSourceTranscriptionWebSocketUri(options);
        var update = Encoding.UTF8.GetString(
            FoundryTranslationProtocol.CreateSourceTranscriptionSessionUpdate(options, "pl"));

        Assert.Equal(
            "wss://glosify-foundry.openai.azure.com/openai/v1/realtime?intent=transcription",
            uri.ToString());
        Assert.Contains("\"model\":\"gpt-realtime-whisper\"", update);
        Assert.Contains("\"language\":\"pl\"", update);
        Assert.Contains("\"delay\":\"medium\"", update);
    }

    [Fact]
    public void SourceAccumulator_PersistsOnlyFinalOriginalSpeech()
    {
        var accumulator = new FoundrySourceTranscriptAccumulator();
        var now = DateTimeOffset.UtcNow;

        Assert.Null(accumulator.Apply(
            "{\"type\":\"conversation.item.input_audio_transcription.delta\",\"item_id\":\"i1\",\"delta\":\"Dzień \"}"u8,
            now));
        var completed = accumulator.Apply(
            "{\"type\":\"conversation.item.input_audio_transcription.completed\",\"item_id\":\"i1\",\"transcript\":\"Dzień dobry\"}"u8,
            now);

        Assert.NotNull(completed);
        Assert.Equal("Dzień dobry", completed.Text);
        Assert.StartsWith("source:item:i1", completed.ProviderEventKey);
        Assert.Null(accumulator.Apply(
            "{\"type\":\"conversation.item.input_audio_transcription.completed\",\"item_id\":\"i1\",\"transcript\":\"Dzień dobry\"}"u8,
            now));
    }

    [Fact]
    public void Protocol_AcceptsOnlyBoundedInputAudioMessages()
    {
        Assert.True(FoundryTranslationProtocol.IsAllowedBrowserMessage(
            "{\"type\":\"session.input_audio_buffer.append\",\"audio\":\"AQIDBA==\"}"u8));
        Assert.False(FoundryTranslationProtocol.IsAllowedBrowserMessage(
            "{\"type\":\"session.update\",\"session\":{}}"u8));
        Assert.False(FoundryTranslationProtocol.IsAllowedBrowserMessage(
            "{\"type\":\"session.input_audio_buffer.append\",\"audio\":\"\"}"u8));
    }

    [Fact]
    public void Protocol_DropsAudioOutputButForwardsTranslationText()
    {
        Assert.True(FoundryTranslationProtocol.ShouldForwardFoundryMessage(
            "{\"type\":\"response.text.delta\",\"text\":\"Hola\"}"u8));
        Assert.False(FoundryTranslationProtocol.ShouldForwardFoundryMessage(
            "{\"type\":\"response.output_audio.delta\",\"delta\":\"AQID\"}"u8));
        Assert.False(FoundryTranslationProtocol.ShouldForwardFoundryMessage(
            "{\"type\":\"session.output_audio.delta\",\"delta\":\"AQID\"}"u8));
        Assert.False(FoundryTranslationProtocol.ShouldForwardFoundryMessage(
            "{\"type\":\"session.input_transcript.delta\",\"delta\":\"Hello\"}"u8));
        Assert.False(FoundryTranslationProtocol.ShouldForwardFoundryMessage(
            "{\"type\":\"conversation.item.input_audio_transcription.completed\",\"transcript\":\"Hello\"}"u8));
    }

    [Fact]
    public void TranscriptAccumulator_PersistsOnlyFinalTranslatedText()
    {
        var accumulator = new FoundryTranslationTranscriptAccumulator();
        var now = DateTimeOffset.UtcNow;

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
        var accumulator = new FoundryTranslationTranscriptAccumulator();
        var start = DateTimeOffset.UtcNow;

        // A caption that only ever arrives as deltas, with no id fields to group on.
        Assert.Null(accumulator.Apply(
            "{\"type\":\"session.output_transcript.delta\",\"delta\":\"Dzień \"}"u8,
            start));
        Assert.Null(accumulator.Apply(
            "{\"type\":\"session.output_transcript.delta\",\"delta\":\"dobry\"}"u8,
            start));
        Assert.Empty(accumulator.FlushIdle(start));

        var flushed = Assert.Single(
            accumulator.FlushIdle(start + FoundryTranslationTranscriptAccumulator.IdleFlush));
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
        var accumulator = new FoundryTranslationTranscriptAccumulator();
        var now = DateTimeOffset.UtcNow;

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
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = CreateTokenStore(cache, clock);
        var sessionId = Guid.NewGuid();
        var grant = store.Create(
            sessionId,
            "user-1",
            "es",
            saveTranscript: true,
            sourceLanguage: "Polish");

        Assert.True(store.TryRedeem(sessionId, grant.Token, out var authorization));
        Assert.Equal("user-1", authorization.UserId);
        Assert.Equal("es", authorization.TargetLanguage);
        Assert.True(authorization.SaveTranscript);
        Assert.Equal("pl", authorization.SourceLanguage);
        Assert.False(store.TryRedeem(sessionId, grant.Token, out _));
    }

    [Fact]
    public void RelayToken_WrongSessionConsumesGrantAndExpiredGrantFails()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = CreateTokenStore(cache, clock);
        var sessionId = Guid.NewGuid();
        var wrongSessionGrant = store.Create(
            sessionId,
            "user-1",
            "es",
            saveTranscript: false,
            sourceLanguage: null);

        Assert.False(store.TryRedeem(Guid.NewGuid(), wrongSessionGrant.Token, out _));
        Assert.False(store.TryRedeem(sessionId, wrongSessionGrant.Token, out _));

        var expiredGrant = store.Create(
            sessionId,
            "user-1",
            "es",
            saveTranscript: false,
            sourceLanguage: null);
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.False(store.TryRedeem(sessionId, expiredGrant.Token, out _));
    }

    [Fact]
    public void RelayToken_RequiresSupportedSourceLanguageWhenSaving()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = CreateTokenStore(cache, TimeProvider.System);

        Assert.Throws<ArgumentException>(() => store.Create(
            Guid.NewGuid(),
            "user-1",
            "es",
            saveTranscript: true,
            sourceLanguage: "sv"));
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
}
