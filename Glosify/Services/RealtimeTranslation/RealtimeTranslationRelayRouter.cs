using System.Net.WebSockets;

namespace Glosify.Services.RealtimeTranslation;

public sealed class RealtimeTranslationRelayRouter(
    IEnhancedTranslationRelay enhancedRelay,
    IScribeTranslationRelay scribeRelay) : IFoundryTranslationRelay
{
    public async Task RelayAsync(
        WebSocket browserSocket,
        RealtimeTranslationRelayAuthorization authorization,
        CancellationToken cancellationToken = default) =>
        await (authorization.TranslationMode switch
        {
            RealtimeTranslationModes.Enhanced =>
                enhancedRelay.RelayAsync(browserSocket, authorization, cancellationToken),
            RealtimeTranslationModes.Scribe =>
                scribeRelay.RelayAsync(browserSocket, authorization, cancellationToken),
            _ => throw new RealtimeTranslationValidationException(
                "The requested live subtitle mode is not supported."),
        });
}
