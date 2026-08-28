using System.Collections.Concurrent;
using System.Threading.Channels;
using Glosify.Services.RealtimeTranslation;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Glosify.Tests;

public sealed class AdaptivePartialTranslationSchedulerTests
{
    private static readonly DateTimeOffset Origin =
        new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FinalWithinInitialDelay_TranslatesOnlyFinal()
    {
        var fixture = new SchedulerFixture();
        fixture.Write(Partial(1, "Good morn"));
        fixture.Write(Final(1, "Good morning"));
        fixture.Complete();

        await fixture.RunAsync();

        var call = Assert.Single(fixture.Translated);
        Assert.True(call.IsFinal);
        Assert.Equal("Good morning", call.Text);
        Assert.Single(fixture.Published);
    }

    [Fact]
    public async Task FinalOnlyMode_IgnoresPartialsAndTranslatesFinal()
    {
        var fixture = new SchedulerFixture(Options(translatePartials: false));
        var run = fixture.RunAsync();
        fixture.Write(Partial(1, "abcdefgh"));
        await fixture.NextObservedAsync();
        Assert.Empty(fixture.Translated);
        fixture.Write(Final(1, "abcdefgh!"));
        fixture.Complete();

        await run;

        var call = Assert.Single(fixture.Translated);
        Assert.True(call.IsFinal);
        Assert.Equal([false, true], fixture.Observed.Select(segment => segment.IsFinal));
    }

    [Fact]
    public async Task ContinuousPartials_StayWithinLatencyFirstCallBudget()
    {
        var fixture = new SchedulerFixture();
        var run = fixture.RunAsync();
        for (var index = 1; index <= 14; index++)
        {
            fixture.Write(Partial(1, new string('a', index * 8)));
            await fixture.NextObservedAsync();
            await fixture.AdvanceAfterTimerAsync(TimeSpan.FromMilliseconds(750));
            if ((index - 2) % 3 == 0)
            {
                await fixture.NextTranslationAsync();
            }
        }
        fixture.Write(Final(1, new string('a', 14 * 8)));
        fixture.Complete();

        await run;

        Assert.InRange(fixture.Translated.Length, 1, 6);
        Assert.True(fixture.Published[^1].Segment.IsFinal);
    }

    [Fact]
    public async Task IntermediatePartials_AreReplacedByLatestText()
    {
        var fixture = new SchedulerFixture(Options(initialDelay: 0));
        fixture.Write(Partial(1, "abcdefgh"));
        fixture.Write(Partial(1, "abcdefghijklmnop"));
        fixture.Write(Partial(1, "abcdefghijklmnopqrstuvwx"));
        var run = fixture.RunAsync();
        var translated = await fixture.NextTranslationAsync();
        fixture.Complete();
        await run;

        Assert.Equal("abcdefghijklmnopqrstuvwx", translated.Text);
        Assert.Single(fixture.Translated);
    }

    [Fact]
    public async Task SlowTranslator_KeepsOneRequestInFlightAndCoalescesQueuedPartials()
    {
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrentRequests = 0;
        var maximumConcurrentRequests = 0;
        var fixture = new SchedulerFixture(
            Options(initialDelay: 0, interval: 2),
            async (segment, cancellationToken) =>
            {
                var concurrent = Interlocked.Increment(ref concurrentRequests);
                maximumConcurrentRequests = Math.Max(maximumConcurrentRequests, concurrent);
                try
                {
                    if (segment.Text == "abcdefgh")
                    {
                        await releaseFirst.Task.WaitAsync(cancellationToken);
                    }
                    return SchedulerFixture.Translation(segment);
                }
                finally
                {
                    Interlocked.Decrement(ref concurrentRequests);
                }
            },
            paceFromRequestStart: true);
        var run = fixture.RunAsync();
        fixture.Write(Partial(1, "abcdefgh"));
        _ = await fixture.NextTranslationAsync();

        fixture.Advance(TimeSpan.FromSeconds(3));
        fixture.Write(Partial(1, "abcdefghijklmnop"));
        fixture.Write(Partial(1, "abcdefghijklmnopqrstuvwx"));
        releaseFirst.TrySetResult();

        var coalesced = await fixture.NextTranslationAsync();
        fixture.Complete();
        await run;

        Assert.Equal("abcdefghijklmnopqrstuvwx", coalesced.Text);
        Assert.Equal(
            ["abcdefgh", "abcdefghijklmnopqrstuvwx"],
            fixture.Translated.Select(segment => segment.Text));
        Assert.Equal(1, maximumConcurrentRequests);
        Assert.Equal(2, fixture.Published.Length);
    }

    [Fact]
    public async Task SmallGrowth_WaitsUntilMaximumStaleness()
    {
        var fixture = new SchedulerFixture(Options(initialDelay: 0));
        var run = fixture.RunAsync();
        fixture.Write(Partial(1, "abcdefgh"));
        await fixture.NextTranslationAsync();

        fixture.Write(Partial(1, "abcdefghi"));
        await fixture.AdvanceAfterTimerAsync(TimeSpan.FromSeconds(2) + TimeSpan.FromMilliseconds(1));
        Assert.Single(fixture.Translated);

        fixture.Advance(TimeSpan.FromSeconds(2));
        var translated = await fixture.NextTranslationAsync();
        fixture.Complete();
        await run;

        Assert.Equal("abcdefghi", translated.Text);
        Assert.Equal(2, fixture.Translated.Length);
    }

    [Fact]
    public async Task Revision_BypassesGrowthThresholdButRespectsInterval()
    {
        var fixture = new SchedulerFixture(Options(initialDelay: 0));
        var run = fixture.RunAsync();
        fixture.Write(Partial(1, "abcdefgh"));
        await fixture.NextTranslationAsync();

        fixture.Write(Partial(1, "abcdXfgh"));
        await fixture.AdvanceAfterTimerAsync(TimeSpan.FromSeconds(1));
        Assert.Single(fixture.Translated);
        fixture.Advance(TimeSpan.FromSeconds(1));
        var translated = await fixture.NextTranslationAsync();
        fixture.Complete();
        await run;

        Assert.Equal("abcdXfgh", translated.Text);
        Assert.Equal(2, fixture.Translated.Length);
    }

    [Fact]
    public async Task PartialIntervalOverride_UsesProviderSpecificCadence()
    {
        var fixture = new SchedulerFixture(
            Options(initialDelay: 0, interval: 2),
            partialInterval: TimeSpan.FromSeconds(1));
        var run = fixture.RunAsync();
        fixture.Write(Partial(1, "abcdefgh"));
        await fixture.NextTranslationAsync();

        fixture.Write(Partial(1, "abcdefghijklmnop"));
        await fixture.AdvanceAfterTimerAsync(TimeSpan.FromSeconds(1));
        var translated = await fixture.NextTranslationAsync();
        fixture.Complete();
        await run;

        Assert.Equal("abcdefghijklmnop", translated.Text);
        Assert.Equal(2, fixture.Translated.Length);
    }

    [Fact]
    public async Task NewSentenceBoundary_BypassesPartialCadence()
    {
        var fixture = new SchedulerFixture(Options(initialDelay: 0, interval: 30));
        var run = fixture.RunAsync();
        fixture.Write(Partial(1, "This is the first sentence"));
        await fixture.NextTranslationAsync();

        fixture.Write(Partial(1, "This is the first sentence. The next one starts"));
        var translated = await fixture.NextTranslationAsync();
        fixture.Complete();
        await run;

        Assert.Equal("This is the first sentence. The next one starts", translated.Text);
        Assert.Equal(2, fixture.Translated.Length);
    }

    [Fact]
    public async Task FirstPartialEndingAtSentenceBoundary_BypassesInitialDelay()
    {
        var fixture = new SchedulerFixture(Options(initialDelay: 30));
        var run = fixture.RunAsync();
        fixture.Write(Partial(1, "A complete sentence."));

        var translated = await fixture.NextTranslationAsync();
        fixture.Complete();
        await run;

        Assert.Equal("A complete sentence.", translated.Text);
        Assert.Single(fixture.Translated);
    }

    [Fact]
    public async Task Final_BypassesPendingTimer()
    {
        var fixture = new SchedulerFixture();
        var run = fixture.RunAsync();
        fixture.Write(Partial(1, "abcdefgh"));
        await fixture.NextTimerCreatedAsync();
        fixture.Write(Final(1, "abcdefgh!"));
        fixture.Complete();

        await run;

        var call = Assert.Single(fixture.Translated);
        Assert.True(call.IsFinal);
    }

    [Fact]
    public async Task IdenticalFinal_ReusesLastPartialTranslation()
    {
        var fixture = new SchedulerFixture(Options(initialDelay: 0));
        var run = fixture.RunAsync();
        fixture.Write(Partial(1, "abcdefgh"));
        await fixture.NextTranslationAsync();
        fixture.Write(Final(1, "abcdefgh"));
        fixture.Complete();

        await run;

        Assert.Single(fixture.Translated);
        Assert.Equal(2, fixture.Published.Length);
        Assert.False(fixture.Published[0].Segment.IsFinal);
        Assert.True(fixture.Published[1].Segment.IsFinal);
        Assert.True(fixture.Published[0].ProviderRequest);
        Assert.False(fixture.Published[1].ProviderRequest);
        Assert.Equal(fixture.Published[0].Result.TranslatedText, fixture.Published[1].Result.TranslatedText);
    }

    [Fact]
    public async Task DifferentFinal_UsesOneFinalTranslationCall()
    {
        var fixture = new SchedulerFixture(Options(initialDelay: 0));
        var run = fixture.RunAsync();
        fixture.Write(Partial(1, "abcdefgh"));
        await fixture.NextTranslationAsync();
        fixture.Write(Final(1, "abcdefgh!"));
        fixture.Complete();

        await run;

        Assert.Equal(2, fixture.Translated.Length);
        Assert.True(fixture.Translated[1].IsFinal);
        Assert.All(fixture.Published, published => Assert.True(published.ProviderRequest));
    }

    [Fact]
    public async Task AutoDetectedLanguage_IsReusedBrieflyAndRefreshedWithinASequence()
    {
        var fixture = new SchedulerFixture(
            Options(initialDelay: 0, interval: 1, languageRefresh: 10),
            translate: (segment, _) => Task.FromResult(new TranslatedSubtitleSegment(
                segment.Sequence,
                segment.Text,
                segment.Text,
                segment.SourceLanguage == "auto" ? "en" : segment.SourceLanguage,
                "en",
                segment.CapturedAt,
                ProviderRequest: segment.SourceLanguage == "auto")));
        var run = fixture.RunAsync();
        fixture.Write(AutoPartial(1, "abcdefgh"));
        var initial = await fixture.NextTranslationAsync();

        fixture.Write(AutoPartial(1, "abcdefghijklmnop"));
        await fixture.AdvanceAfterTimerAsync(TimeSpan.FromSeconds(1));
        var cached = await fixture.NextTranslationAsync();

        fixture.Advance(TimeSpan.FromSeconds(10));
        fixture.Write(AutoPartial(1, "abcdefghijklmnopqrstuvwx"));
        var refreshed = await fixture.NextTranslationAsync();
        fixture.Complete();
        await run;

        Assert.Equal("auto", initial.SourceLanguage);
        Assert.Equal("en", cached.SourceLanguage);
        Assert.Equal("auto", refreshed.SourceLanguage);
        Assert.Equal(
            [true, false, true],
            fixture.Published.Select(published => published.ProviderRequest));
    }

    [Fact]
    public async Task AutoDetectedLanguageCache_ResetsForEachProviderSequence()
    {
        var fixture = new SchedulerFixture(
            Options(initialDelay: 0),
            translate: (segment, _) => Task.FromResult(new TranslatedSubtitleSegment(
                segment.Sequence,
                segment.Text,
                segment.Text,
                "en",
                "en",
                segment.CapturedAt,
                ProviderRequest: segment.SourceLanguage == "auto")));
        var run = fixture.RunAsync();
        fixture.Write(AutoPartial(1, "abcdefgh"));
        _ = await fixture.NextTranslationAsync();
        fixture.Write(AutoPartial(2, "ijklmnop"));
        var nextSequence = await fixture.NextTranslationAsync();
        fixture.Complete();
        await run;

        Assert.Equal("auto", nextSequence.SourceLanguage);
        Assert.All(fixture.Published, published => Assert.True(published.ProviderRequest));
    }

    [Fact]
    public async Task CancellationAndCompletion_LeaveNoDeferredTranslation()
    {
        var cancelled = new SchedulerFixture();
        using var cancellation = new CancellationTokenSource();
        var cancelledRun = cancelled.RunAsync(cancellation.Token);
        cancelled.Write(Partial(1, "abcdefgh"));
        await cancelled.NextTimerCreatedAsync();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRun);
        cancelled.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(cancelled.Translated);

        var completed = new SchedulerFixture();
        completed.Write(Partial(1, "abcdefgh"));
        completed.Complete();
        await completed.RunAsync();
        completed.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(completed.Translated);
    }

    [Fact]
    public async Task TranslatorFailure_PropagatesWithoutRetry()
    {
        var expected = new InvalidOperationException("translator failed");
        var attempts = 0;
        var fixture = new SchedulerFixture(
            translate: (_, _) =>
            {
                attempts++;
                return Task.FromException<TranslatedSubtitleSegment>(expected);
            });
        fixture.Write(Final(1, "abcdefgh"));
        fixture.Complete();

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunAsync());

        Assert.Same(expected, actual);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task OptionalPartialFailure_DoesNotPreventFinalTranslation()
    {
        var fixture = new SchedulerFixture(
            Options(initialDelay: 0),
            (segment, _) => segment.IsFinal
                ? Task.FromResult(SchedulerFixture.Translation(segment))
                : Task.FromException<TranslatedSubtitleSegment>(
                    new RealtimeTranslationUpstreamException("partial failed")),
            ignorePartialTranslationFailures: true);
        var run = fixture.RunAsync();
        fixture.Write(Partial(1, "abcdefgh"));
        _ = await fixture.NextTranslationAsync();
        fixture.Write(Final(1, "abcdefgh!"));
        fixture.Complete();

        await run;

        Assert.Equal(2, fixture.Translated.Length);
        var published = Assert.Single(fixture.Published);
        Assert.True(published.Segment.IsFinal);
        Assert.Equal("abcdefgh!", published.Segment.Text);
    }

    private static ElevenLabsRealtimeSpeechOptions Options(
        bool translatePartials = true,
        double initialDelay = 1,
        double interval = 2,
        int minimumGrowth = 8,
        double languageRefresh = 10) => new()
        {
            TranslatePartials = translatePartials,
            PartialInitialDelaySeconds = initialDelay,
            PartialIntervalSeconds = interval,
            PartialMinimumGrowthCharacters = minimumGrowth,
            AutoDetectedLanguageRefreshSeconds = languageRefresh,
        };

    private static RecognizedSpeechSegment Partial(int sequence, string text) =>
        new(sequence, text, "en", "en-US", Origin, IsFinal: false);

    private static RecognizedSpeechSegment Final(int sequence, string text) =>
        new(sequence, text, "en", "en-US", Origin, IsFinal: true);

    private static RecognizedSpeechSegment AutoPartial(int sequence, string text) =>
        new(
            sequence,
            text,
            "auto",
            "auto",
            Origin,
            IsAutoDetected: true,
            IsFinal: false);

    private sealed class SchedulerFixture
    {
        private readonly Channel<RecognizedSpeechSegment> _input =
            Channel.CreateUnbounded<RecognizedSpeechSegment>();
        private readonly Channel<RecognizedSpeechSegment> _observedEvents =
            Channel.CreateUnbounded<RecognizedSpeechSegment>();
        private readonly Channel<RecognizedSpeechSegment> _translationEvents =
            Channel.CreateUnbounded<RecognizedSpeechSegment>();
        private readonly AdaptivePartialTranslationScheduler _scheduler;
        private readonly Func<RecognizedSpeechSegment, CancellationToken, Task<TranslatedSubtitleSegment>> _translate;
        private readonly ConcurrentQueue<RecognizedSpeechSegment> _translated = new();
        private readonly ConcurrentQueue<RecognizedSpeechSegment> _observed = new();
        private readonly ConcurrentQueue<(
            RecognizedSpeechSegment Segment,
            TranslatedSubtitleSegment Result,
            bool ProviderRequest)> _published = new();

        public SchedulerFixture(
            ElevenLabsRealtimeSpeechOptions? options = null,
            Func<RecognizedSpeechSegment, CancellationToken, Task<TranslatedSubtitleSegment>>? translate = null,
            TimeSpan? partialInterval = null,
            bool paceFromRequestStart = false,
            bool ignorePartialTranslationFailures = false)
        {
            Clock = new NotifyingTimeProvider(Origin);
            _scheduler = new AdaptivePartialTranslationScheduler(
                Clock,
                options ?? Options(),
                partialInterval: partialInterval,
                paceFromRequestStart: paceFromRequestStart,
                ignorePartialTranslationFailures: ignorePartialTranslationFailures);
            _translate = translate ?? CreateTranslationAsync;
        }

        public NotifyingTimeProvider Clock { get; }
        public RecognizedSpeechSegment[] Translated => _translated.ToArray();
        public RecognizedSpeechSegment[] Observed => _observed.ToArray();
        public (
            RecognizedSpeechSegment Segment,
            TranslatedSubtitleSegment Result,
            bool ProviderRequest)[] Published => _published.ToArray();

        public Task RunAsync(CancellationToken cancellationToken = default) =>
            _scheduler.RunAsync(
                _input.Reader,
                TranslateAndRecordAsync,
                (segment, result, providerRequest, _) =>
                {
                    _published.Enqueue((segment, result, providerRequest));
                    return Task.CompletedTask;
                },
                cancellationToken,
                (segment, _) =>
                {
                    _observed.Enqueue(segment);
                    Assert.True(_observedEvents.Writer.TryWrite(segment));
                    return Task.CompletedTask;
                });

        public void Write(RecognizedSpeechSegment segment) => Assert.True(_input.Writer.TryWrite(segment));

        public void Complete() => _input.Writer.TryComplete();

        public void Advance(TimeSpan amount) => Clock.Advance(amount);

        public async Task AdvanceAfterTimerAsync(TimeSpan amount)
        {
            await NextTimerCreatedAsync();
            Clock.Advance(amount);
        }

        public Task<RecognizedSpeechSegment> NextObservedAsync() =>
            _observedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        public Task<RecognizedSpeechSegment> NextTranslationAsync() =>
            _translationEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        public Task<TimeSpan> NextTimerCreatedAsync() => Clock.NextTimerCreatedAsync();

        private async Task<TranslatedSubtitleSegment> TranslateAndRecordAsync(
            RecognizedSpeechSegment segment,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _translated.Enqueue(segment);
            Assert.True(_translationEvents.Writer.TryWrite(segment));
            return await _translate(segment, cancellationToken);
        }

        private static Task<TranslatedSubtitleSegment> CreateTranslationAsync(
            RecognizedSpeechSegment segment,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Translation(segment));
        }

        internal static TranslatedSubtitleSegment Translation(RecognizedSpeechSegment segment) =>
            new(
                segment.Sequence,
                segment.Text,
                $"translated:{segment.Text}",
                segment.SourceLanguage,
                "sv",
                segment.CapturedAt);
    }

    private sealed class NotifyingTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new(start);
        private readonly Channel<TimeSpan> _createdTimers = Channel.CreateUnbounded<TimeSpan>();

        public override TimeZoneInfo LocalTimeZone => _clock.LocalTimeZone;

        public override long TimestampFrequency => _clock.TimestampFrequency;

        public void Advance(TimeSpan amount) => _clock.Advance(amount);

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = _clock.CreateTimer(callback, state, dueTime, period);
            Assert.True(_createdTimers.Writer.TryWrite(dueTime));
            return timer;
        }

        public override long GetTimestamp() => _clock.GetTimestamp();

        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();

        public Task<TimeSpan> NextTimerCreatedAsync() =>
            _createdTimers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
