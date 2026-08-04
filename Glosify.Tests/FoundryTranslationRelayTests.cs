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
    }

    [Fact]
    public void Protocol_UsesWhisperTranscriptionEndpointWithoutLanguageHint()
    {
        var options = new RealtimeTranslationOptions
        {
            FoundryEndpoint = "https://glosify-foundry.openai.azure.com/",
            SourceTranscriptionDeployment = "gpt-realtime-whisper",
            SourceTranscriptionDelay = "medium",
        };

        var uri = FoundryTranslationProtocol.BuildSourceTranscriptionWebSocketUri(options);
        var update = Encoding.UTF8.GetString(
            FoundryTranslationProtocol.CreateSourceTranscriptionSessionUpdate(options));

        Assert.Equal(
            "wss://glosify-foundry.openai.azure.com/openai/v1/realtime?intent=transcription",
            uri.ToString());
        Assert.Contains("\"model\":\"gpt-realtime-whisper\"", update);
        Assert.Contains("\"delay\":\"medium\"", update);
        Assert.DoesNotContain("\"language\"", update);
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
        Assert.Null(accumulator.Apply(
            "{\"type\":\"response.output_text.done\",\"response_id\":\"r1\",\"text\":\"Hola mundo\"}"u8,
            now));
    }

    [Fact]
    public void RelayToken_IsSingleUseAndBoundToSession()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = CreateTokenStore(cache, clock);
        var sessionId = Guid.NewGuid();
        var grant = store.Create(sessionId, "user-1", "es", saveTranscript: true);

        Assert.True(store.TryRedeem(sessionId, grant.Token, out var authorization));
        Assert.Equal("user-1", authorization.UserId);
        Assert.Equal("es", authorization.TargetLanguage);
        Assert.True(authorization.SaveTranscript);
        Assert.False(store.TryRedeem(sessionId, grant.Token, out _));
    }

    [Fact]
    public void RelayToken_WrongSessionConsumesGrantAndExpiredGrantFails()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = CreateTokenStore(cache, clock);
        var sessionId = Guid.NewGuid();
        var wrongSessionGrant = store.Create(sessionId, "user-1", "es", saveTranscript: false);

        Assert.False(store.TryRedeem(Guid.NewGuid(), wrongSessionGrant.Token, out _));
        Assert.False(store.TryRedeem(sessionId, wrongSessionGrant.Token, out _));

        var expiredGrant = store.Create(sessionId, "user-1", "es", saveTranscript: false);
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.False(store.TryRedeem(sessionId, expiredGrant.Token, out _));
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
