using System.Net.WebSockets;
using Glosify.Services.RealtimeTranslation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers.Api;

[ApiController]
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[Route("api/realtime-translation/sessions/{sessionId:guid}/stream")]
public sealed class RealtimeTranslationRelayController : ControllerBase
{
    private readonly IRealtimeTranslationRelayTokenStore _tokens;
    private readonly IRealtimeTranslationRelay _relay;
    private readonly IRealtimeTranslationService _sessions;
    private readonly ILogger<RealtimeTranslationRelayController> _logger;

    public RealtimeTranslationRelayController(
        IRealtimeTranslationRelayTokenStore tokens,
        IRealtimeTranslationRelay relay,
        IRealtimeTranslationService sessions,
        ILogger<RealtimeTranslationRelayController> logger)
    {
        _tokens = tokens;
        _relay = relay;
        _sessions = sessions;
        _logger = logger;
    }

    public async Task Stream(Guid sessionId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var relayToken = ReadRelayToken(HttpContext.WebSockets.WebSocketRequestedProtocols);
        if (relayToken is null
            || !_tokens.TryRedeem(sessionId, relayToken, out var authorization))
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var browserSocket = await HttpContext.WebSockets.AcceptWebSocketAsync(
            OpenAiTranslationProtocol.BrowserProtocol);
        try
        {
            await _relay.RelayAsync(browserSocket, authorization, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Browser disconnect or application shutdown.
        }
        catch (RealtimeTranslationExpiredException exception)
        {
            _logger.LogInformation(
                "Subtitle relay authorization ended for session {SessionId}",
                sessionId);
            await SendErrorAndCloseAsync(browserSocket, exception.Message);
        }
        catch (RealtimeTranslationUnavailableException exception)
        {
            await FailSessionQuietlyAsync(authorization);
            await SendErrorAndCloseAsync(browserSocket, exception.Message);
        }
        catch (RealtimeTranslationUpstreamException exception)
        {
            await FailSessionQuietlyAsync(authorization);
            await SendErrorAndCloseAsync(browserSocket, exception.Message);
        }
    }

    private async Task FailSessionQuietlyAsync(RealtimeTranslationRelayAuthorization authorization)
    {
        try
        {
            await _sessions.FailSessionAsync(
                authorization.UserId,
                authorization.SessionId,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not finalize failed subtitle relay session {SessionId}",
                authorization.SessionId);
        }
    }

    internal static string? ReadRelayToken(IList<string> requestedProtocols)
    {
        string? token = null;
        foreach (var protocol in requestedProtocols)
        {
            if (!protocol.StartsWith(
                    OpenAiTranslationProtocol.RelayTokenProtocolPrefix,
                    StringComparison.Ordinal))
            {
                continue;
            }
            if (token is not null)
            {
                return null;
            }
            token = protocol[OpenAiTranslationProtocol.RelayTokenProtocolPrefix.Length..];
        }
        return RealtimeTranslationRelayTokenStore.IsValidToken(token) ? token : null;
    }

    private static async Task SendErrorAndCloseAsync(WebSocket socket, string message)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }
        try
        {
            await OpenAiTranslationRelay.SendBrowserControlAsync(
                socket,
                "glosify.relay.error",
                message,
                CancellationToken.None);
            await socket.CloseOutputAsync(
                WebSocketCloseStatus.PolicyViolation,
                "Subtitle relay authorization ended.",
                CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // The browser already closed the connection.
        }
    }
}
