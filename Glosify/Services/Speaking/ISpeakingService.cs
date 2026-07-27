namespace Glosify.Services.Speaking;

public interface ISpeakingService
{
    Task<SpeakingSessionCreated> CreateSessionAsync(
        string userId,
        SpeakingAvatarDefinition avatar,
        CefrLevel cefrLevel,
        CancellationToken cancellationToken = default);

    Task<SpeakingSessionCreated> CreateSessionAsync(
        string userId,
        SpeakingAvatarDefinition avatar,
        CefrLevel cefrLevel,
        Guid? quizId,
        CancellationToken cancellationToken = default) =>
        quizId is null
            ? CreateSessionAsync(userId, avatar, cefrLevel, cancellationToken)
            : throw new SpeakingValidationException("This speaking service does not support quiz context.");

    Task<SpeakingTurn> SendTurnAsync(
        Guid sessionId,
        string userId,
        string text,
        SpeakingInputMode inputMode,
        CancellationToken cancellationToken = default);

    Task<SpeakingTurn> SendTurnAsync(
        Guid sessionId,
        string userId,
        string text,
        SpeakingInputMode inputMode,
        Guid? practicePromptId,
        CancellationToken cancellationToken = default) =>
        practicePromptId is null
            ? SendTurnAsync(sessionId, userId, text, inputMode, cancellationToken)
            : throw new SpeakingValidationException("This speaking service does not support practice prompts.");

    Task<SpeakingActiveQuiz?> SelectQuizAsync(
        Guid sessionId,
        string userId,
        Guid? quizId,
        CancellationToken cancellationToken = default) =>
        throw new SpeakingValidationException("This speaking service does not support quiz selection.");

    Task<SpeakingTurn> SendActionAsync(
        Guid sessionId,
        string userId,
        SpeakingInteractionAction action,
        IReadOnlyDictionary<int, int>? denominations,
        string? drinkId = null,
        CancellationToken cancellationToken = default);

    Task DeleteSessionAsync(
        Guid sessionId,
        string userId,
        CancellationToken cancellationToken = default);
}
