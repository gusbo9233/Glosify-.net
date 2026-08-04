namespace Glosify.Services.RealtimeTranslation;

public interface IRealtimeTranslationService
{
    Task<RealtimeTranslationCatalog> GetCatalogAsync(
        string userId,
        CancellationToken cancellationToken = default);

    Task<RealtimeTranslationSessionCreated> CreateSessionAsync(
        string userId,
        string targetLanguage,
        bool saveTranscript = false,
        Guid? transcriptId = null,
        CancellationToken cancellationToken = default);

    Task<RealtimeTranslationMinuteResult> ReserveMinuteAsync(
        string userId,
        Guid sessionId,
        int minuteIndex,
        CancellationToken cancellationToken = default);

    Task<RealtimeTranslationMinuteResult> BeginMinuteAsync(
        string userId,
        Guid sessionId,
        int minuteIndex,
        CancellationToken cancellationToken = default);

    Task<RealtimeTranslationSessionStatus> HeartbeatAsync(
        string userId,
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task EndSessionAsync(
        string userId,
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task FailSessionAsync(
        string userId,
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task CleanupStaleSessionsAsync(CancellationToken cancellationToken = default);
}
