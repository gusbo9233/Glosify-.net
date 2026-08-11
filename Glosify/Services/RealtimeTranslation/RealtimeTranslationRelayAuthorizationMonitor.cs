using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glosify.Services.RealtimeTranslation;

public sealed class RealtimeTranslationRelayAuthorizationMonitor(
    IServiceScopeFactory scopeFactory,
    IOptions<RealtimeTranslationOptions> options,
    TimeProvider timeProvider,
    ILogger<RealtimeTranslationRelayAuthorizationMonitor> logger)
{
    private const int PcmBytesPerSecond = 24_000 * sizeof(short);
    private readonly RealtimeTranslationOptions _options = options.Value;

    public async Task<RealtimeTranslationRelaySessionState> WaitForSessionStartAsync(
        RealtimeTranslationRelayAuthorization authorization,
        CancellationToken cancellationToken)
    {
        var deadline = timeProvider.GetUtcNow().AddSeconds(_options.RelayStartupTimeoutSeconds);
        while (timeProvider.GetUtcNow() < deadline)
        {
            var state = await LoadSessionStateAsync(authorization, cancellationToken);
            if (state is null || IsTerminal(state.Status) || state.ExpiresAt <= timeProvider.GetUtcNow())
            {
                throw new RealtimeTranslationExpiredException(
                    "The live subtitle session ended before audio started.");
            }
            if (state.StartedAt is not null && state.ChargedMinutes >= 1)
            {
                return state;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), timeProvider, cancellationToken);
        }

        throw new RealtimeTranslationExpiredException(
            "The first live subtitle minute was not authorized in time.");
    }

    public async Task MonitorAuthorizationAsync(
        RealtimeTranslationRelayAuthorization authorization,
        DateTimeOffset startedAt,
        RealtimeTranslationRelayBillingState billing,
        CancellationToken cancellationToken)
    {
        var consecutiveDatabaseFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var now = timeProvider.GetUtcNow();
                var state = await LoadSessionStateAsync(authorization, cancellationToken);
                if (state is null
                    || IsTerminal(state.Status)
                    || state.ExpiresAt <= now
                    || state.LastHeartbeatAt < now.AddSeconds(-Math.Max(30, _options.StaleSessionSeconds)))
                {
                    throw new RealtimeTranslationExpiredException(
                        "The live subtitle session is no longer authorized.");
                }

                var expectedMinute = Math.Max(1, (int)Math.Floor((now - startedAt).TotalMinutes) + 1);
                if (expectedMinute > _options.MaxSessionMinutes)
                {
                    throw new RealtimeTranslationExpiredException(
                        "The live subtitle session reached its time limit.");
                }

                var boundary = startedAt.AddMinutes(expectedMinute - 1);
                if (state.ChargedMinutes < expectedMinute
                    && now > boundary.AddSeconds(_options.RelayBillingGraceSeconds))
                {
                    throw new RealtimeTranslationExpiredException(
                        "The current live subtitle minute was not authorized.");
                }

                Volatile.Write(ref billing.ChargedMinutes, state.ChargedMinutes);
                consecutiveDatabaseFailures = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (RealtimeTranslationExpiredException)
            {
                throw;
            }
            catch (Exception exception)
            {
                consecutiveDatabaseFailures++;
                logger.LogWarning(
                    exception,
                    "Could not verify billing for subtitle relay session {SessionId}",
                    authorization.SessionId);
                if (consecutiveDatabaseFailures >= 3)
                {
                    throw new RealtimeTranslationUpstreamException(
                        "Glosify could not verify the live subtitle session.");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, cancellationToken);
        }
    }

    public async Task WaitForAudioCapacityAsync(
        long requestedBytes,
        DateTimeOffset startedAt,
        RealtimeTranslationRelayBillingState billing,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var paidBytes = (long)Volatile.Read(ref billing.ChargedMinutes)
                * 60
                * PcmBytesPerSecond;
            var elapsedSeconds = Math.Max(0, (timeProvider.GetUtcNow() - startedAt).TotalSeconds);
            var realtimeBytes = (long)((elapsedSeconds + 2) * PcmBytesPerSecond);
            if (requestedBytes <= Math.Min(paidBytes, realtimeBytes))
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeProvider, cancellationToken);
        }
    }

    private async Task<RealtimeTranslationRelaySessionState?> LoadSessionStateAsync(
        RealtimeTranslationRelayAuthorization authorization,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<GlosifyContext>();
        return await context.RealtimeTranslationSessions
            .AsNoTracking()
            .Where(session => session.Id == authorization.SessionId
                && session.UserId == authorization.UserId
                && session.TargetLanguage == authorization.TargetLanguage
                && session.TranslationMode == authorization.TranslationMode
                && session.SourceLanguage == authorization.SourceLanguage
                && (session.TranscriptId != null) == authorization.SaveTranscript)
            .Select(session => new RealtimeTranslationRelaySessionState(
                session.Status,
                session.StartedAt,
                session.LastHeartbeatAt,
                session.ExpiresAt,
                session.ChargedMinutes,
                session.TranscriptId))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static bool IsTerminal(string status) =>
        status is RealtimeTranslationSessionStatuses.Completed
            or RealtimeTranslationSessionStatuses.Interrupted
            or RealtimeTranslationSessionStatuses.Failed;
}

public sealed record RealtimeTranslationRelaySessionState(
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset LastHeartbeatAt,
    DateTimeOffset ExpiresAt,
    int ChargedMinutes,
    Guid? TranscriptId);

public sealed class RealtimeTranslationRelayBillingState(int chargedMinutes)
{
    public int ChargedMinutes = chargedMinutes;
}
