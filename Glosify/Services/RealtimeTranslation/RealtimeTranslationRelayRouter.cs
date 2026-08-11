using System.Net.WebSockets;

namespace Glosify.Services.RealtimeTranslation;

public sealed class RealtimeTranslationRelayRouter(
    IEnhancedTranslationRelay enhancedRelay,
    IEconomicalTranslationRelay economicalRelay) : IFoundryTranslationRelay
{
    public Task RelayAsync(
        WebSocket browserSocket,
        RealtimeTranslationRelayAuthorization authorization,
        CancellationToken cancellationToken = default) =>
        authorization.TranslationMode switch
        {
            RealtimeTranslationModes.Enhanced =>
                enhancedRelay.RelayAsync(browserSocket, authorization, cancellationToken),
            RealtimeTranslationModes.Economical =>
                economicalRelay.RelayAsync(browserSocket, authorization, cancellationToken),
            _ => throw new RealtimeTranslationValidationException(
                "The requested live subtitle mode is not supported."),
        };
}
