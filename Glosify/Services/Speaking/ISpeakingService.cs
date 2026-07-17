namespace Glosify.Services.Speaking;

public interface ISpeakingService
{
    Task<SpeakingSessionCreated> CreateSessionAsync(
        string userId,
        SpeakingAvatarDefinition avatar,
        CefrLevel cefrLevel,
        CancellationToken cancellationToken = default);

    Task<SpeakingTurn> SendTurnAsync(
        Guid sessionId,
        string userId,
        string text,
        SpeakingInputMode inputMode,
        CancellationToken cancellationToken = default);

    Task DeleteSessionAsync(
        Guid sessionId,
        string userId,
        CancellationToken cancellationToken = default);
}
