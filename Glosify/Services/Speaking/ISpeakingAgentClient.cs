namespace Glosify.Services.Speaking;

public interface ISpeakingAgentClient
{
    bool IsConfigured { get; }

    Task<ISpeakingAgentConversation> CreateConversationAsync(
        SpeakingAvatarId avatar,
        bool interactiveMode = false,
        CancellationToken cancellationToken = default);
}

public interface ISpeakingAgentConversation : IAsyncDisposable
{
    Task<SpeakingAgentTurn> RunTurnAsync(
        string message,
        BartenderInteractionState? interactionState = null,
        CancellationToken cancellationToken = default);

    Task<SpeakingAgentTurn> RunTurnAsync(
        string message,
        BartenderInteractionState? interactionState,
        CancellationToken cancellationToken,
        SpeakingQuizContextState? quizContext) =>
        quizContext is null
            ? RunTurnAsync(message, interactionState, cancellationToken)
            : throw new SpeakingValidationException("This conversation does not support quiz tools.");
}
