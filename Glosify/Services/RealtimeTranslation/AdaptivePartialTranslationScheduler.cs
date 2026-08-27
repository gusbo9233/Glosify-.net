using System.Text;
using System.Threading.Channels;

namespace Glosify.Services.RealtimeTranslation;

internal sealed class AdaptivePartialTranslationScheduler
{
    private static readonly KeyValuePair<string, object?> PartialKind = new("caption.kind", "partial");
    private static readonly KeyValuePair<string, object?> FinalKind = new("caption.kind", "final");

    private readonly TimeProvider _timeProvider;
    private readonly bool _translatePartials;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _interval;
    private readonly int _minimumGrowthCharacters;
    private readonly TimeSpan _autoDetectedLanguageRefresh;

    public AdaptivePartialTranslationScheduler(
        TimeProvider timeProvider,
        ElevenLabsRealtimeSpeechOptions options)
    {
        _timeProvider = timeProvider;
        _translatePartials = options.TranslatePartials;
        _initialDelay = TimeSpan.FromSeconds(options.PartialInitialDelaySeconds);
        _interval = TimeSpan.FromSeconds(options.PartialIntervalSeconds);
        _minimumGrowthCharacters = options.PartialMinimumGrowthCharacters;
        _autoDetectedLanguageRefresh = TimeSpan.FromSeconds(
            options.AutoDetectedLanguageRefreshSeconds);
    }

    public async Task RunAsync(
        ChannelReader<RecognizedSpeechSegment> recognized,
        Func<RecognizedSpeechSegment, CancellationToken, Task<TranslatedSubtitleSegment>> translate,
        Func<RecognizedSpeechSegment, TranslatedSubtitleSegment, bool, CancellationToken, Task> publish,
        CancellationToken cancellationToken,
        Func<RecognizedSpeechSegment, CancellationToken, Task>? observeSource = null)
    {
        RecognizedSpeechSegment? pendingPartial = null;
        DateTimeOffset? pendingSince = null;
        DateTimeOffset? lastPartialSubmittedAt = null;
        string? lastSubmittedText = null;
        TranslatedSubtitleSegment? lastTranslation = null;
        int? activeSequence = null;
        var smallGrowthRecorded = false;
        var inputCompleted = false;
        var detectedLanguage = new AutoDetectedLanguageCache(
            _autoDetectedLanguageRefresh);

        while (!inputCompleted)
        {
            while (recognized.TryRead(out var segment))
            {
                if (observeSource is not null)
                {
                    await observeSource(segment, cancellationToken);
                }
                if (segment.IsFinal)
                {
                    if (pendingPartial is not null)
                    {
                        RecordSuppression("superseded_by_final");
                    }
                    pendingPartial = null;
                    pendingSince = null;
                    smallGrowthRecorded = false;

                    if (activeSequence == segment.Sequence
                        && lastTranslation is not null
                        && string.Equals(lastSubmittedText, segment.Text, StringComparison.Ordinal))
                    {
                        RecordSuppression("final_cache_hit");
                        var reused = lastTranslation with
                        {
                            Sequence = segment.Sequence,
                            SourceText = segment.Text,
                            SourceLanguage = segment.SourceLanguage,
                            CapturedAt = segment.CapturedAt,
                            ProviderRequest = false,
                        };
                        await publish(segment, reused, false, cancellationToken);
                    }
                    else
                    {
                        var result = await TranslateAsync(
                            segment,
                            translate,
                            FinalKind,
                            cancellationToken);
                        detectedLanguage.Observe(
                            segment,
                            result,
                            _timeProvider.GetUtcNow());
                        await publish(
                            segment,
                            result,
                            result.ProviderRequest,
                            cancellationToken);
                    }

                    activeSequence = null;
                    lastPartialSubmittedAt = null;
                    lastSubmittedText = null;
                    lastTranslation = null;
                    continue;
                }

                if (!_translatePartials)
                {
                    continue;
                }

                if (activeSequence != segment.Sequence)
                {
                    if (pendingPartial is not null)
                    {
                        RecordSuppression("throttled");
                    }
                    activeSequence = segment.Sequence;
                    pendingPartial = null;
                    pendingSince = null;
                    lastPartialSubmittedAt = null;
                    lastSubmittedText = null;
                    lastTranslation = null;
                    smallGrowthRecorded = false;
                    detectedLanguage.Reset(segment.Sequence);
                }

                var comparisonText = pendingPartial?.Text ?? lastSubmittedText;
                if (string.Equals(comparisonText, segment.Text, StringComparison.Ordinal))
                {
                    RecordSuppression("duplicate");
                    continue;
                }

                if (pendingPartial is not null)
                {
                    RecordSuppression("throttled");
                }
                pendingPartial = segment;
                pendingSince ??= _timeProvider.GetUtcNow();
                smallGrowthRecorded = false;
            }

            if (pendingPartial is not null)
            {
                var now = _timeProvider.GetUtcNow();
                var cadenceDue = lastPartialSubmittedAt is null
                    ? pendingSince!.Value + _initialDelay
                    : lastPartialSubmittedAt.Value + _interval;
                var stalenessDue = lastPartialSubmittedAt is null
                    ? pendingSince!.Value + (_interval * 2)
                    : lastPartialSubmittedAt.Value + (_interval * 2);
                var meaningfulChange = IsMeaningfulChange(
                    lastSubmittedText,
                    pendingPartial.Text,
                    _minimumGrowthCharacters);

                if (!meaningfulChange && now >= cadenceDue && !smallGrowthRecorded)
                {
                    RecordSuppression("small_growth");
                    smallGrowthRecorded = true;
                }

                var translationDue = meaningfulChange ? cadenceDue : stalenessDue;
                if (now >= translationDue)
                {
                    var partial = pendingPartial;
                    pendingPartial = null;
                    pendingSince = null;
                    smallGrowthRecorded = false;
                    var translationInput = detectedLanguage.Apply(
                        partial,
                        _timeProvider.GetUtcNow());
                    var result = await TranslateAsync(
                        translationInput,
                        translate,
                        PartialKind,
                        cancellationToken);
                    detectedLanguage.Observe(
                        partial,
                        result,
                        _timeProvider.GetUtcNow());
                    await publish(
                        partial,
                        result,
                        result.ProviderRequest,
                        cancellationToken);
                    lastPartialSubmittedAt = _timeProvider.GetUtcNow();
                    lastSubmittedText = partial.Text;
                    lastTranslation = result;
                    continue;
                }

                var wait = translationDue - now;
                inputCompleted = await WaitForInputOrDelayAsync(
                    recognized,
                    wait,
                    cancellationToken) == WaitOutcome.InputCompleted;
                continue;
            }

            inputCompleted = !await recognized.WaitToReadAsync(cancellationToken);
        }

        await recognized.Completion;
    }

    private static async Task<TranslatedSubtitleSegment> TranslateAsync(
        RecognizedSpeechSegment segment,
        Func<RecognizedSpeechSegment, CancellationToken, Task<TranslatedSubtitleSegment>> translate,
        KeyValuePair<string, object?> kind,
        CancellationToken cancellationToken)
    {
        var result = await translate(segment, cancellationToken);
        if (result.ProviderRequest)
        {
            RealtimeTranslationTelemetry.TranslationRequests.Add(1, kind);
            RealtimeTranslationTelemetry.TranslatedCharacters.Add(result.SourceText.Length, kind);
        }
        return result;
    }

    private sealed class AutoDetectedLanguageCache(TimeSpan refreshInterval)
    {
        private int? _sequence;
        private string? _language;
        private DateTimeOffset _refreshAt;

        public RecognizedSpeechSegment Apply(
            RecognizedSpeechSegment segment,
            DateTimeOffset now)
        {
            EnsureSequence(segment.Sequence);
            return segment.IsAutoDetected
                && !string.IsNullOrWhiteSpace(_language)
                && now < _refreshAt
                    ? segment with { SourceLanguage = _language }
                    : segment;
        }

        public void Observe(
            RecognizedSpeechSegment source,
            TranslatedSubtitleSegment result,
            DateTimeOffset now)
        {
            EnsureSequence(source.Sequence);
            if (!source.IsAutoDetected
                || !result.ProviderRequest
                || string.IsNullOrWhiteSpace(result.SourceLanguage)
                || string.Equals(
                    result.SourceLanguage,
                    "auto",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _language = result.SourceLanguage;
            _refreshAt = now + refreshInterval;
        }

        public void Reset(int sequence)
        {
            _sequence = sequence;
            _language = null;
            _refreshAt = default;
        }

        private void EnsureSequence(int sequence)
        {
            if (_sequence != sequence)
            {
                Reset(sequence);
            }
        }
    }

    private async Task<WaitOutcome> WaitForInputOrDelayAsync(
        ChannelReader<RecognizedSpeechSegment> recognized,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return WaitOutcome.DelayElapsed;
        }

        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var inputTask = recognized.WaitToReadAsync(waitCancellation.Token).AsTask();
        var delayTask = Task.Delay(delay, _timeProvider, waitCancellation.Token);
        var completed = await Task.WhenAny(inputTask, delayTask);
        await waitCancellation.CancelAsync();

        if (completed == inputTask)
        {
            try
            {
                return await inputTask ? WaitOutcome.InputAvailable : WaitOutcome.InputCompleted;
            }
            finally
            {
                await ObserveCancellationAsync(delayTask);
            }
        }

        try
        {
            await delayTask;
            return WaitOutcome.DelayElapsed;
        }
        finally
        {
            await ObserveCancellationAsync(inputTask);
        }
    }

    private static bool IsMeaningfulChange(string? previous, string current, int minimumGrowthCharacters)
    {
        if (previous is null)
        {
            return CountRunes(current) >= minimumGrowthCharacters;
        }
        if (!current.StartsWith(previous, StringComparison.Ordinal))
        {
            return true;
        }
        return CountRunes(current[previous.Length..]) >= minimumGrowthCharacters;
    }

    private static int CountRunes(string value) => value.EnumerateRunes().Count();

    private static void RecordSuppression(string reason) =>
        RealtimeTranslationTelemetry.PartialTranslationsSuppressed.Add(
            1,
            new KeyValuePair<string, object?>("reason", reason));

    private static async Task ObserveCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // The losing wait is cancelled and observed before the scheduler continues.
        }
    }

    private enum WaitOutcome
    {
        InputAvailable,
        InputCompleted,
        DelayElapsed,
    }
}
