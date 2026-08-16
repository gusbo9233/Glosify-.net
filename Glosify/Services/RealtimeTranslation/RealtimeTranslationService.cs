using Glosify.Data;
using Glosify.Infrastructure.Concurrency;
using Glosify.Models.Entities;
using Glosify.Services.Ai;
using Glosify.Services.Language;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glosify.Services.RealtimeTranslation;

public sealed class RealtimeTranslationService : IRealtimeTranslationService
{
    private readonly GlosifyContext _context;
    private readonly IAiCreditService _credits;
    private readonly IRealtimeTranslationRelayTokenStore _relayTokens;
    private readonly IQuizLanguagePreferenceService _languagePreferences;
    private readonly IRealtimeTranslationLanguageCatalog _languageCatalog;
    private readonly RealtimeTranslationOptions _options;
    private readonly ICreditPricingResolver _pricing;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RealtimeTranslationService> _logger;
    private readonly IKeyedAsyncLock _keyedLock;

    public RealtimeTranslationService(
        GlosifyContext context,
        IAiCreditService credits,
        IRealtimeTranslationRelayTokenStore relayTokens,
        IQuizLanguagePreferenceService languagePreferences,
        IRealtimeTranslationLanguageCatalog languageCatalog,
        IOptions<RealtimeTranslationOptions> options,
        ICreditPricingResolver pricing,
        TimeProvider timeProvider,
        ILogger<RealtimeTranslationService> logger,
        IKeyedAsyncLock keyedLock)
    {
        _context = context;
        _credits = credits;
        _relayTokens = relayTokens;
        _languagePreferences = languagePreferences;
        _languageCatalog = languageCatalog;
        _options = options.Value;
        _pricing = pricing;
        _timeProvider = timeProvider;
        _logger = logger;
        _keyedLock = keyedLock;
    }

    public async Task<RealtimeTranslationCatalog> GetCatalogAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        var account = await _credits.GetOrCreateAccountAsync(userId, cancellationToken);
        var selectedQuizLanguage = await _languagePreferences.GetSelectedAsync(userId, cancellationToken);
        if (selectedQuizLanguage is { IsLanguageLearning: false })
        {
            selectedQuizLanguage = null;
        }
        var languages = await _languageCatalog.GetLanguagesAsync(cancellationToken);
        var quizLanguages = QuizLanguageCatalog.LanguageLearning
            .Select(language => new RealtimeTranslationLanguage(language.Code, language.Name))
            .ToArray();
        var modes = new List<RealtimeTranslationMode>();
        if (_options.ElevenLabs.Enabled)
        {
            modes.Add(new RealtimeTranslationMode(
                RealtimeTranslationModes.Scribe,
                "ElevenLabs Scribe v2",
                "Scribe v2 speech recognition with Azure translation",
                _pricing.ScribeSubtitleCreditsPerStartedMinute));
        }
        modes.Add(new RealtimeTranslationMode(
            RealtimeTranslationModes.Enhanced,
            "Enhanced",
            "Best translation quality",
            _pricing.EnhancedSubtitleCreditsPerStartedMinute));
        var sourceLanguages = languages
            .Select(language => new RealtimeTranslationSourceLanguage(
                NormalizeScribeHint(language.Code),
                language.Name,
                null))
            .Where(language => language.Code.Length is 2 or 3
                && language.Code.All(char.IsAsciiLetterLower))
            .DistinctBy(language => language.Code, StringComparer.OrdinalIgnoreCase)
            .OrderBy(language => language.Name, StringComparer.OrdinalIgnoreCase)
            .Prepend(new RealtimeTranslationSourceLanguage("auto", "Auto detect", null))
            .ToArray();
        return new RealtimeTranslationCatalog(
            languages,
            quizLanguages,
            _pricing.EnhancedSubtitleCreditsPerStartedMinute,
            _pricing.EnhancedWithTranscriptCreditsPerStartedMinute,
            _options.SavedSourceTranscriptsEnabled,
            selectedQuizLanguage is null
                ? null
                : new RealtimeTranslationSelectedQuizLanguage(selectedQuizLanguage.Code, selectedQuizLanguage.Name),
            _options.MaxSessionMinutes,
            _options.RenewalLeadSeconds,
            _options.HeartbeatSeconds,
            _options.Model,
            account.AvailableCredits,
            modes,
            sourceLanguages);
    }

    public async Task<RealtimeTranslationSessionCreated> CreateSessionAsync(
        string userId,
        string targetLanguage,
        bool saveTranscript = false,
        Guid? transcriptId = null,
        string? translationMode = null,
        string? sourceLanguage = null,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        var languages = await _languageCatalog.GetLanguagesAsync(cancellationToken);
        var language = languages.FirstOrDefault(item =>
                string.Equals(item.Code, targetLanguage?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new RealtimeTranslationValidationException("Choose a supported target language.");
        var mode = string.IsNullOrWhiteSpace(translationMode)
            ? RealtimeTranslationModes.Enhanced
            : translationMode.Trim().ToLowerInvariant();
        if (mode is not (RealtimeTranslationModes.Scribe or RealtimeTranslationModes.Enhanced))
        {
            throw new RealtimeTranslationValidationException(
                "Choose ElevenLabs Scribe v2 or Enhanced subtitles.");
        }
        if (mode == RealtimeTranslationModes.Scribe && !_options.ElevenLabs.Enabled)
        {
            throw new RealtimeTranslationUnavailableException(
                "ElevenLabs Scribe v2 is not enabled on this Glosify deployment.");
        }
        var canonicalSpeechProvider = mode switch
        {
            RealtimeTranslationModes.Scribe => RealtimeSpeechProviders.ElevenLabs,
            _ => RealtimeSpeechProviders.Foundry,
        };
        var usesScribe = mode == RealtimeTranslationModes.Scribe || saveTranscript;
        var canonicalSourceLanguage = usesScribe
            ? string.IsNullOrWhiteSpace(sourceLanguage)
                || string.Equals(sourceLanguage.Trim(), "auto", StringComparison.OrdinalIgnoreCase)
                    ? "auto"
                    : NormalizeScribeHint(sourceLanguage)
            : null;
        if (canonicalSourceLanguage is not null and not "auto"
            && !languages.Select(language => NormalizeScribeHint(language.Code)).Contains(
                canonicalSourceLanguage,
                StringComparer.OrdinalIgnoreCase))
        {
            throw new RealtimeTranslationValidationException(
                "Choose a supported spoken-language hint or use automatic detection.");
        }
        QuizLanguage? selectedQuizLanguage = null;
        if (saveTranscript)
        {
            if (!_options.SavedSourceTranscriptsEnabled)
            {
                throw new RealtimeTranslationUnavailableException(
                    "Saved source transcripts are not enabled on this Glosify deployment.");
            }
            selectedQuizLanguage = await _languagePreferences.GetSelectedAsync(userId, cancellationToken);
            if (selectedQuizLanguage is null || !selectedQuizLanguage.IsLanguageLearning)
            {
                throw new RealtimeTranslationValidationException(
                    "Choose a quiz language in Glosify before saving an original speech transcript.");
            }
            if (!_options.ElevenLabs.Enabled)
            {
                throw new RealtimeTranslationUnavailableException(
                    "Saved transcripts require ElevenLabs Scribe v2 on this Glosify deployment.");
            }
        }
        await using (await _keyedLock.AcquireAsync("user:" + userId, cancellationToken))
        {
            var hasActiveSession = await _context.RealtimeTranslationSessions.AnyAsync(session =>
                session.UserId == userId
                && (session.Status == RealtimeTranslationSessionStatuses.Pending
                    || session.Status == RealtimeTranslationSessionStatuses.Active),
                cancellationToken);
            if (hasActiveSession)
            {
                throw new RealtimeTranslationConflictException(
                    "This account already has an active live subtitle session.");
            }

            var now = _timeProvider.GetUtcNow();
            if (!saveTranscript && transcriptId.HasValue)
            {
                throw new RealtimeTranslationValidationException(
                    "A saved transcript can only be continued when transcript storage is enabled.");
            }

            RealtimeTranslationTranscript? transcript = null;
            if (saveTranscript && transcriptId.HasValue)
            {
                transcript = await _context.RealtimeTranslationTranscripts.SingleOrDefaultAsync(item =>
                    item.Id == transcriptId.Value
                    && item.UserId == userId,
                    cancellationToken)
                    ?? throw new RealtimeTranslationNotFoundException("Saved transcript not found.");
                if (!string.Equals(
                        transcript.TargetLanguage,
                        selectedQuizLanguage!.Code,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new RealtimeTranslationValidationException(
                        "A saved transcript cannot change learning language during reconnect.");
                }
                if (!string.Equals(transcript.Stream, RealtimeTranslationTranscriptStreams.Source, StringComparison.Ordinal))
                {
                    throw new RealtimeTranslationValidationException(
                        "A legacy translated transcript cannot be continued as a source transcript.");
                }
            }
            else if (saveTranscript)
            {
                transcript = new RealtimeTranslationTranscript
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    TargetLanguage = selectedQuizLanguage!.Code,
                    Stream = RealtimeTranslationTranscriptStreams.Source,
                    Title = $"{selectedQuizLanguage.Name} source transcript · {now:dd MMM yyyy, HH:mm}",
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                _context.RealtimeTranslationTranscripts.Add(transcript);
            }

            var session = new RealtimeTranslationSession
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TargetLanguage = language.Code,
                TranslationMode = mode,
                SpeechProvider = canonicalSpeechProvider,
                SourceLanguage = canonicalSourceLanguage,
                Model = mode == RealtimeTranslationModes.Scribe
                    ? _options.ElevenLabs.Model
                    : _options.Deployment,
                SourceTranscriptionDeployment = mode == RealtimeTranslationModes.Enhanced && transcript is not null
                    ? _options.ElevenLabs.Model
                    : null,
                BillingModel = mode == RealtimeTranslationModes.Scribe
                    ? _options.ElevenLabs.BillingModel
                    : transcript is null
                        ? _options.Deployment
                        : _options.SavedTranscriptBillingModel,
                CreditsPerStartedMinute = mode == RealtimeTranslationModes.Scribe
                    ? _pricing.ScribeSubtitleCreditsPerStartedMinute
                    : transcript is null
                        ? _pricing.EnhancedSubtitleCreditsPerStartedMinute
                        : _pricing.EnhancedWithTranscriptCreditsPerStartedMinute,
                TranscriptId = transcript?.Id,
                TranscriptConsentAt = transcript is null ? null : now,
                Status = RealtimeTranslationSessionStatuses.Pending,
                CreatedAt = now,
                LastHeartbeatAt = now,
                ExpiresAt = now.AddMinutes(_options.MaxSessionMinutes),
            };

            AiDurationCreditReservation reservation;
            try
            {
                reservation = await ReserveCreditsAsync(session, 1, cancellationToken);
            }
            catch (InsufficientAiCreditsException)
            {
                RealtimeTranslationTelemetry.CreditFailures.Add(1);
                throw;
            }

            var minute = BuildMinute(session.Id, 1, reservation, now);
            session.Minutes.Add(minute);
            _context.RealtimeTranslationSessions.Add(session);
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                _context.Entry(minute).State = EntityState.Detached;
                _context.Entry(session).State = EntityState.Detached;
                if (transcript is not null && _context.Entry(transcript).State == EntityState.Added)
                {
                    _context.Entry(transcript).State = EntityState.Detached;
                }
                await _credits.ReleaseAsync(reservation.ReservationId, cancellationToken);
                throw;
            }

            try
            {
                var grant = _relayTokens.Create(
                    session.Id,
                    userId,
                    language.Code,
                    mode,
                    canonicalSpeechProvider,
                    canonicalSourceLanguage,
                    transcript is not null,
                    selectedQuizLanguage?.Code);
                var account = await _credits.GetOrCreateAccountAsync(userId, cancellationToken);
                RealtimeTranslationTelemetry.SessionsCreated.Add(1);
                return new RealtimeTranslationSessionCreated(
                    session.Id,
                    grant.Token,
                    grant.ExpiresAt,
                    $"/api/realtime-translation/sessions/{session.Id:D}/stream",
                    1,
                    account.AvailableCredits,
                    session.CreditsPerStartedMinute,
                    session.TranscriptId);
            }
            catch
            {
                await _credits.ReleaseAsync(reservation.ReservationId, cancellationToken);
                minute.Status = RealtimeTranslationMinuteStatuses.Released;
                minute.ReleasedAt = _timeProvider.GetUtcNow();
                session.Status = RealtimeTranslationSessionStatuses.Failed;
                session.EndedAt = minute.ReleasedAt;
                await _context.SaveChangesAsync(cancellationToken);
                throw;
            }
        }
    }

    private static string NormalizeScribeHint(string code) =>
        code.Trim().Split('-', 2)[0].ToLowerInvariant();

    public async Task<RealtimeTranslationMinuteResult> ReserveMinuteAsync(
        string userId,
        Guid sessionId,
        int minuteIndex,
        CancellationToken cancellationToken = default)
    {
        EnsureMinuteIndex(minuteIndex);
        return await WithSessionLockAsync(sessionId, async () =>
        {
            var session = await LoadOwnedSessionAsync(userId, sessionId, cancellationToken);
            EnsureSessionCanContinue(session);
            var existing = session.Minutes.SingleOrDefault(minute => minute.MinuteIndex == minuteIndex);
            if (existing is not null)
            {
                return await BuildMinuteResultAsync(session, existing, cancellationToken);
            }
            if (minuteIndex != session.ChargedMinutes + 1)
            {
                throw new RealtimeTranslationConflictException(
                    "Subtitle minutes must be reserved in order.");
            }

            AiDurationCreditReservation reservation;
            try
            {
                reservation = await ReserveCreditsAsync(session, minuteIndex, cancellationToken);
            }
            catch (InsufficientAiCreditsException)
            {
                RealtimeTranslationTelemetry.CreditFailures.Add(1);
                throw;
            }

            var minute = BuildMinute(session.Id, minuteIndex, reservation, _timeProvider.GetUtcNow());
            _context.RealtimeTranslationMinutes.Add(minute);
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                _context.Entry(minute).State = EntityState.Detached;
                await _credits.ReleaseAsync(reservation.ReservationId, cancellationToken);
                throw;
            }

            return await BuildMinuteResultAsync(session, minute, cancellationToken);
        }, cancellationToken);
    }

    public async Task<RealtimeTranslationMinuteResult> BeginMinuteAsync(
        string userId,
        Guid sessionId,
        int minuteIndex,
        CancellationToken cancellationToken = default)
    {
        EnsureMinuteIndex(minuteIndex);
        return await WithSessionLockAsync(sessionId, async () =>
        {
            var session = await LoadOwnedSessionAsync(userId, sessionId, cancellationToken);
            EnsureSessionCanContinue(session);
            var minute = session.Minutes.SingleOrDefault(item => item.MinuteIndex == minuteIndex)
                ?? throw new RealtimeTranslationConflictException(
                    "Reserve this subtitle minute before starting it.");
            if (minute.Status == RealtimeTranslationMinuteStatuses.Begun)
            {
                return await BuildMinuteResultAsync(session, minute, cancellationToken);
            }
            if (minuteIndex != session.ChargedMinutes + 1)
            {
                throw new RealtimeTranslationConflictException(
                    "Subtitle minutes must be started in order.");
            }
            if (minute.Status != RealtimeTranslationMinuteStatuses.Reserved)
            {
                throw new RealtimeTranslationExpiredException("This subtitle minute is no longer available.");
            }

            await _credits.CommitDurationUsageAsync(minute.ReservationId, 60, cancellationToken);
            var now = _timeProvider.GetUtcNow();
            minute.Status = RealtimeTranslationMinuteStatuses.Begun;
            minute.BegunAt = now;
            session.Status = RealtimeTranslationSessionStatuses.Active;
            session.StartedAt ??= now;
            session.LastHeartbeatAt = now;
            if (minuteIndex == 1)
            {
                session.ExpiresAt = now.AddMinutes(_options.MaxSessionMinutes);
            }
            session.ChargedMinutes += 1;
            session.CreditsCharged += minute.Credits;
            await _context.SaveChangesAsync(cancellationToken);
            RealtimeTranslationTelemetry.MinutesCharged.Add(1);
            if (minuteIndex == 1)
            {
                RealtimeTranslationTelemetry.SessionsStarted.Add(1);
            }
            return await BuildMinuteResultAsync(session, minute, cancellationToken);
        }, cancellationToken);
    }

    public async Task<RealtimeTranslationSessionStatus> HeartbeatAsync(
        string userId,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        return await WithSessionLockAsync(sessionId, async () =>
        {
            var session = await LoadOwnedSessionAsync(userId, sessionId, cancellationToken);
            EnsureSessionCanContinue(session);
            session.LastHeartbeatAt = _timeProvider.GetUtcNow();
            await _context.SaveChangesAsync(cancellationToken);
            return MapStatus(session);
        }, cancellationToken);
    }

    public async Task EndSessionAsync(
        string userId,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await WithSessionLockAsync(sessionId, async () =>
        {
            var session = await LoadOwnedSessionAsync(userId, sessionId, cancellationToken);
            await EndSessionCoreAsync(session, RealtimeTranslationSessionStatuses.Completed, cancellationToken);
            return true;
        }, cancellationToken);
    }

    public async Task FailSessionAsync(
        string userId,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await WithSessionLockAsync(sessionId, async () =>
        {
            var session = await LoadOwnedSessionAsync(userId, sessionId, cancellationToken);
            await EndSessionCoreAsync(session, RealtimeTranslationSessionStatuses.Failed, cancellationToken);
            return true;
        }, cancellationToken);
    }

    public async Task CleanupStaleSessionsAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var staleHeartbeat = now.AddSeconds(-Math.Max(30, _options.StaleSessionSeconds));
        var staleReservation = now.AddSeconds(-Math.Max(30, _options.ReservationExpirySeconds));
        var sessionIds = await _context.RealtimeTranslationSessions
            .AsNoTracking()
            .Where(session =>
                (session.Status == RealtimeTranslationSessionStatuses.Pending
                    && session.CreatedAt < staleReservation)
                || (session.Status == RealtimeTranslationSessionStatuses.Active
                    && (session.LastHeartbeatAt < staleHeartbeat || session.ExpiresAt <= now)))
            .Select(session => session.Id)
            .ToListAsync(cancellationToken);

        foreach (var sessionId in sessionIds)
        {
            try
            {
                await WithSessionLockAsync(sessionId, async () =>
                {
                    var session = await _context.RealtimeTranslationSessions
                        .Include(item => item.Minutes)
                        .SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
                    if (session is not null
                        && (session.Status == RealtimeTranslationSessionStatuses.Pending
                            || session.Status == RealtimeTranslationSessionStatuses.Active))
                    {
                        await EndSessionCoreAsync(
                            session,
                            RealtimeTranslationSessionStatuses.Interrupted,
                            cancellationToken);
                    }
                    return true;
                }, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Could not clean up stale translation session {SessionId}", sessionId);
            }
        }

        var emptyTranscripts = await _context.RealtimeTranslationTranscripts
            .Where(transcript => transcript.CreatedAt < now.AddHours(-24)
                && !transcript.Segments.Any()
                && !transcript.Sessions.Any(session =>
                    session.Status == RealtimeTranslationSessionStatuses.Pending
                    || session.Status == RealtimeTranslationSessionStatuses.Active))
            .Include(transcript => transcript.Sessions)
            .ToListAsync(cancellationToken);
        foreach (var transcript in emptyTranscripts)
        {
            foreach (var session in transcript.Sessions)
            {
                session.TranscriptId = null;
            }
        }
        _context.RealtimeTranslationTranscripts.RemoveRange(emptyTranscripts);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task EndSessionCoreAsync(
        RealtimeTranslationSession session,
        string terminalStatus,
        CancellationToken cancellationToken)
    {
        if (session.Status is RealtimeTranslationSessionStatuses.Completed
            or RealtimeTranslationSessionStatuses.Interrupted
            or RealtimeTranslationSessionStatuses.Failed)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        foreach (var minute in session.Minutes.Where(item => item.Status == RealtimeTranslationMinuteStatuses.Reserved))
        {
            await _credits.ReleaseAsync(minute.ReservationId, cancellationToken);
            minute.Status = RealtimeTranslationMinuteStatuses.Released;
            minute.ReleasedAt = now;
        }
        session.Status = terminalStatus;
        session.EndedAt = now;
        session.LastHeartbeatAt = now;
        await _context.SaveChangesAsync(cancellationToken);
        RealtimeTranslationTelemetry.SessionsEnded.Add(
            1,
            new KeyValuePair<string, object?>("status", terminalStatus));
    }

    private Task<AiDurationCreditReservation> ReserveCreditsAsync(
        RealtimeTranslationSession session,
        int minuteIndex,
        CancellationToken cancellationToken) =>
        _credits.ReserveDurationAsync(
            new AiUsageContext(
                session.UserId,
                AiUsageFeatures.RealtimeTranslation,
                "subtitle_minute",
                session.Id,
                "RealtimeTranslationSession",
                $"{session.Id:N}:{minuteIndex}"),
            session.SpeechProvider == RealtimeSpeechProviders.ElevenLabs
                ? RealtimeTranslationConstants.ElevenLabsProvider
                : RealtimeTranslationConstants.Provider,
            session.BillingModel,
            60,
            session.CreditsPerStartedMinute,
            cancellationToken);

    private static RealtimeTranslationMinute BuildMinute(
        Guid sessionId,
        int minuteIndex,
        AiDurationCreditReservation reservation,
        DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            MinuteIndex = minuteIndex,
            ReservationId = reservation.ReservationId,
            Credits = reservation.ReservedCredits,
            Status = RealtimeTranslationMinuteStatuses.Reserved,
            ReservedAt = now,
        };

    private async Task<RealtimeTranslationSession> LoadOwnedSessionAsync(
        string userId,
        Guid sessionId,
        CancellationToken cancellationToken) =>
        await _context.RealtimeTranslationSessions
            .Include(session => session.Minutes)
            .SingleOrDefaultAsync(session => session.Id == sessionId && session.UserId == userId, cancellationToken)
        ?? throw new RealtimeTranslationNotFoundException("Live subtitle session not found.");

    private void EnsureSessionCanContinue(RealtimeTranslationSession session)
    {
        if (session.Status is RealtimeTranslationSessionStatuses.Completed
            or RealtimeTranslationSessionStatuses.Interrupted
            or RealtimeTranslationSessionStatuses.Failed
            || session.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            throw new RealtimeTranslationExpiredException("The live subtitle session has ended.");
        }
    }

    private void EnsureMinuteIndex(int minuteIndex)
    {
        if (minuteIndex < 1 || minuteIndex > _options.MaxSessionMinutes)
        {
            throw new RealtimeTranslationExpiredException("The live subtitle session reached its time limit.");
        }
    }

    private void EnsureEnabled()
    {
        if (!_options.Enabled)
        {
            throw new RealtimeTranslationUnavailableException("Live subtitles are not enabled on this Glosify deployment.");
        }
    }

    private async Task<RealtimeTranslationMinuteResult> BuildMinuteResultAsync(
        RealtimeTranslationSession session,
        RealtimeTranslationMinute minute,
        CancellationToken cancellationToken)
    {
        var account = await _credits.GetOrCreateAccountAsync(session.UserId, cancellationToken);
        return new RealtimeTranslationMinuteResult(
            session.Id,
            minute.MinuteIndex,
            minute.Status,
            account.AvailableCredits,
            session.ChargedMinutes,
            session.CreditsCharged,
            session.StartedAt,
            GetAudioSendAuthorizedUntil(session));
    }

    private static RealtimeTranslationSessionStatus MapStatus(RealtimeTranslationSession session) =>
        new(
            session.Id,
            session.Status,
            session.ExpiresAt,
            session.ChargedMinutes,
            session.CreditsCharged,
            session.StartedAt,
            GetAudioSendAuthorizedUntil(session));

    private static DateTimeOffset? GetAudioSendAuthorizedUntil(RealtimeTranslationSession session)
    {
        if (session.StartedAt is not { } startedAt)
        {
            return null;
        }

        var authorizedMinute = session.Minutes
            .Where(minute => minute.Status is RealtimeTranslationMinuteStatuses.Reserved
                or RealtimeTranslationMinuteStatuses.Begun)
            .Select(minute => minute.MinuteIndex)
            .DefaultIfEmpty(session.ChargedMinutes)
            .Max();
        return authorizedMinute > 0 ? startedAt.AddMinutes(authorizedMinute) : null;
    }

    private async Task<T> WithSessionLockAsync<T>(
        Guid sessionId,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        await using var lease = await _keyedLock.AcquireAsync(
            "session:" + sessionId.ToString("N"),
            cancellationToken);
        return await action();
    }
}
