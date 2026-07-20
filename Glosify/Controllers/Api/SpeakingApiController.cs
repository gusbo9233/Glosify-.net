using Glosify.Extensions;
using Glosify.Filters;
using Glosify.Models.Api;
using Glosify.Services.Language;
using Glosify.Services.Speaking;
using Glosify.Services.Speech;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers.Api;

[ApiController]
[Authorize]
[AiServiceExceptionFilter]
[Route("api/speaking")]
public sealed class SpeakingApiController : ControllerBase
{
    private readonly ISpeakingService _speaking;
    private readonly ISpeechAuthorizationTokenService _speechTokens;
    private readonly ILanguageContext _languageContext;

    public SpeakingApiController(
        ISpeakingService speaking,
        ISpeechAuthorizationTokenService speechTokens,
        ILanguageContext languageContext)
    {
        _speaking = speaking;
        _speechTokens = speechTokens;
        _languageContext = languageContext;
    }

    [HttpPost("speech-token")]
    public async Task<IActionResult> SpeechToken(CancellationToken cancellationToken)
    {
        var token = await _speechTokens.GetTokenAsync(cancellationToken);
        Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        Response.Headers.Pragma = "no-cache";
        return Ok(new
        {
            token.AuthorizationToken,
            token.Region,
            token.ExpiresAtUtc,
        });
    }

    [HttpPost("sessions")]
    public async Task<IActionResult> CreateSession(
        [FromBody] CreateSpeakingSessionRequest request,
        CancellationToken cancellationToken)
    {
        var language = _languageContext.CurrentLanguage;
        if (language is null)
        {
            return BadRequest(new { error = "Select a language before starting speaking practice." });
        }

        if (!SpeakingAvatarCatalog.TryParseForLanguage(request.AvatarId, language, out var avatar))
        {
            return BadRequest(new { error = $"That avatar is not available for {language}." });
        }

        if (!SpeakingAvatarCatalog.TryParseCefr(request.CefrLevel, out var cefrLevel))
        {
            return BadRequest(new { error = "CEFR level must be A1, A2, B1, B2, or C1." });
        }

        var created = await _speaking.CreateSessionAsync(
            User.GetUserId(),
            avatar,
            cefrLevel,
            cancellationToken);
        return Ok(created);
    }

    [HttpPost("sessions/{sessionId:guid}/turns")]
    public async Task<IActionResult> SendTurn(
        Guid sessionId,
        [FromBody] SendSpeakingTurnRequest request,
        CancellationToken cancellationToken)
    {
        if (!SpeakingAvatarCatalog.TryParseInputMode(request.InputMode, out var inputMode))
        {
            return BadRequest(new { error = "Input mode must be voice or text." });
        }

        var turn = await _speaking.SendTurnAsync(
            sessionId,
            User.GetUserId(),
            request.Text ?? string.Empty,
            inputMode,
            cancellationToken);
        return Ok(turn);
    }

    [HttpPost("sessions/{sessionId:guid}/actions")]
    public async Task<IActionResult> SendAction(
        Guid sessionId,
        [FromBody] SendSpeakingActionRequest request,
        CancellationToken cancellationToken)
    {
        if (!BartenderInteractionCatalog.TryParseUserAction(request.Action, out var action))
        {
            return BadRequest(new
            {
                error = "Action must be drink, takeSnack, or submitPayment.",
            });
        }

        var turn = await _speaking.SendActionAsync(
            sessionId,
            User.GetUserId(),
            action,
            request.Denominations,
            cancellationToken);
        return Ok(turn);
    }

    [HttpDelete("sessions/{sessionId:guid}")]
    public async Task<IActionResult> DeleteSession(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await _speaking.DeleteSessionAsync(
            sessionId,
            User.GetUserId(),
            cancellationToken);
        return NoContent();
    }
}
