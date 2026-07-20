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
}
