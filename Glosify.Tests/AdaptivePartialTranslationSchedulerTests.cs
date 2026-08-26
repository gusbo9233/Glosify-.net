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
        await fixture.AdvanceAsync(TimeSpan.FromMinutes(1));
        Assert.Empty(fixture.Translated);
        fixture.Write(Final(1, "abcdefgh!"));
        fixture.Complete();

        await run;

        var call = Assert.Single(fixture.Translated);
        Assert.True(call.IsFinal);
    }

    [Fact]
    public async Task ContinuousPartials_StayWithinLatencyFirstCallBudget()
    {
        var fixture = new SchedulerFixture();
        var run = fixture.RunAsync();
        for (var index = 1; index <= 14; index++)
        {
            fixture.Write(Partial(1, new string('a', index * 8)));
            await fixture.AdvanceAsync(TimeSpan.FromMilliseconds(750));
        }
        fixture.Write(Final(1, new string('a', 14 * 8)));
        fixture.Complete();

        await run;

        Assert.InRange(fixture.Translated.Count, 1, 6);
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
        await WaitForAsync(() => fixture.Translated.Count == 1);
        fixture.Complete();
        await run;

        Assert.Equal("abcdefghijklmnopqrstuvwx", fixture.Translated[0].Text);
    }

    [Fact]
    public async Task SmallGrowth_WaitsUntilMaximumStaleness()
    {
        var fixture = new SchedulerFixture(Options(initialDelay: 0));
        var run = fixture.RunAsync();
        fixture.Write(Partial(1, "abcdefgh"));
        await WaitForAsync(() => fixture.Translated.Count == 1);

        fixture.Write(Partial(1, "abcdefghi"));
        await YieldAsync();
        await fixture.AdvanceAsync(TimeSpan.FromSeconds(2) + TimeSpan.FromMilliseconds(1));
        Assert.Single(fixture.Translated);

        await fixture.AdvanceAsync(TimeSpan.FromSeconds(2));
        await WaitForAsync(() => fixture.Translated.Count == 2);
        fixture.Complete();
        await run;

        Assert.Equal("abcdefghi", fixture.Translated[1].Text);
    }

    [Fact]
    public async Task Revision_BypassesGrowthThresholdButRespectsInterval()
    {
        var fixture = new SchedulerFixture(Options(initialDelay: 0));
        var run = fixture.RunAsync();
        fixture.Write(Partial(1, "abcdefgh"));
        await WaitForAsync(() => fixture.Translated.Count == 1);

        fixture.Write(Partial(1, "abcdXfgh"));
        await fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Single(fixture.Translated);
        await fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        await WaitForAsync(() => fixture.Translated.Count == 2);
        fixture.Complete();
        await run;

        Assert.Equal("abcdXfgh", fixture.Translated[1].Text);
    }

    [Fact]
    public async Task Final_BypassesPendingTimer()
    {
        var fixture = new SchedulerFixture();
        var run = fixture.RunAsync();
        fixture.Write(Partial(1, "abcdefgh"));
        await YieldAsync();
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
        await WaitForAsync(() => fixture.Translated.Count == 1);
        fixture.Write(Final(1, "abcdefgh"));
        fixture.Complete();

        await run;

        Assert.Single(fixture.Translated);
        Assert.Equal(2, fixture.Published.Count);
        Assert.False(fixture.Published[0].Segment.IsFinal);
        Assert.True(fixture.Published[1].Segment.IsFinal);
        Assert.Equal(fixture.Published[0].Result.TranslatedText, fixture.Published[1].Result.TranslatedText);
    }

    [Fact]
    public async Task DifferentFinal_UsesOneFinalTranslationCall()
    {
        var fixture = new SchedulerFixture(Options(initialDelay: 0));
        var run = fixture.RunAsync();
        fixture.Write(Partial(1, "abcdefgh"));
        await WaitForAsync(() => fixture.Translated.Count == 1);
        fixture.Write(Final(1, "abcdefgh!"));
        fixture.Complete();

        await run;

        Assert.Equal(2, fixture.Translated.Count);
        Assert.True(fixture.Translated[1].IsFinal);
    }

    [Fact]
    public async Task CancellationAndCompletion_LeaveNoDeferredTranslation()
    {
        var cancelled = new SchedulerFixture();
        using var cancellation = new CancellationTokenSource();
        var cancelledRun = cancelled.RunAsync(cancellation.Token);
        cancelled.Write(Partial(1, "abcdefgh"));
        await YieldAsync();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRun);
        await cancelled.AdvanceAsync(TimeSpan.FromMinutes(1));
        Assert.Empty(cancelled.Translated);

        var completed = new SchedulerFixture();
        completed.Write(Partial(1, "abcdefgh"));
        completed.Complete();
        await completed.RunAsync();
        await completed.AdvanceAsync(TimeSpan.FromMinutes(1));
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

    private static ElevenLabsRealtimeSpeechOptions Options(
        bool translatePartials = true,
        double initialDelay = 1,
        double interval = 2,
        int minimumGrowth = 8) => new()
        {
            TranslatePartials = translatePartials,
            PartialInitialDelaySeconds = initialDelay,
            PartialIntervalSeconds = interval,
            PartialMinimumGrowthCharacters = minimumGrowth,
        };

    private static RecognizedSpeechSegment Partial(int sequence, string text) =>
        new(sequence, text, "en", "en-US", Origin, IsFinal: false);

    private static RecognizedSpeechSegment Final(int sequence, string text) =>
        new(sequence, text, "en", "en-US", Origin, IsFinal: true);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 1_000 && !condition(); attempt++)
        {
            await Task.Delay(1).ConfigureAwait(false);
        }
        Assert.True(condition(), "The scheduler did not reach the expected state.");
    }

    private static Task YieldAsync() => Task.Delay(10);

    private sealed class SchedulerFixture
    {
        private readonly Channel<RecognizedSpeechSegment> _input =
            Channel.CreateUnbounded<RecognizedSpeechSegment>();
        private readonly AdaptivePartialTranslationScheduler _scheduler;
        private readonly Func<RecognizedSpeechSegment, CancellationToken, Task<TranslatedSubtitleSegment>> _translate;

        public SchedulerFixture(
            ElevenLabsRealtimeSpeechOptions? options = null,
            Func<RecognizedSpeechSegment, CancellationToken, Task<TranslatedSubtitleSegment>>? translate = null)
        {
            Clock = new FakeTimeProvider(Origin);
            _scheduler = new AdaptivePartialTranslationScheduler(Clock, options ?? Options());
            _translate = translate ?? TranslateAsync;
        }

        public FakeTimeProvider Clock { get; }
        public List<RecognizedSpeechSegment> Translated { get; } = [];
        public List<(RecognizedSpeechSegment Segment, TranslatedSubtitleSegment Result)> Published { get; } = [];

        public Task RunAsync(CancellationToken cancellationToken = default) =>
            _scheduler.RunAsync(
                _input.Reader,
                _translate,
                (segment, result, _) =>
                {
                    Published.Add((segment, result));
                    return Task.CompletedTask;
                },
                cancellationToken);

        public void Write(RecognizedSpeechSegment segment) => Assert.True(_input.Writer.TryWrite(segment));

        public void Complete() => _input.Writer.TryComplete();

        public async Task AdvanceAsync(TimeSpan amount)
        {
            Clock.Advance(amount);
            await YieldAsync().ConfigureAwait(false);
        }

        private Task<TranslatedSubtitleSegment> TranslateAsync(
            RecognizedSpeechSegment segment,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Translated.Add(segment);
            return Task.FromResult(new TranslatedSubtitleSegment(
                segment.Sequence,
                segment.Text,
                $"translated:{segment.Text}",
                segment.SourceLanguage,
                "sv",
                segment.CapturedAt));
        }
    }
}
