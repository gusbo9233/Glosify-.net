using System.Net.WebSockets;

namespace Glosify.Services.RealtimeTranslation;

public interface IFoundryTranslationRelay
{
    Task RelayAsync(
        WebSocket browserSocket,
        RealtimeTranslationRelayAuthorization authorization,
        CancellationToken cancellationToken = default);
}

public interface IEnhancedTranslationRelay
{
    Task RelayAsync(
        WebSocket browserSocket,
        RealtimeTranslationRelayAuthorization authorization,
        CancellationToken cancellationToken = default);
}
