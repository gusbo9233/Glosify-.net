namespace Glosify.Services.Speaking;

public interface ISpeakingAgentClient
{
    bool IsConfigured { get; }

    Task<ISpeakingAgentConversation> CreateConversationAsync(
        SpeakingAvatarId avatar,
        CancellationToken cancellationToken = default);
}

public interface ISpeakingAgentConversation : IAsyncDisposable
{
    Task<SpeakingAgentTurn> RunTurnAsync(
        string message,
        CancellationToken cancellationToken = default);
}
