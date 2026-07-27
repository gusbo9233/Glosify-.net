using Glosify.Extensions;
using Glosify.Filters;
using Glosify.Models.Api;
using Glosify.Services.Language;
using Glosify.Services.Speaking;
using Glosify.Services.Speech;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

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
    private readonly SpeakingOptions _options;

    public SpeakingApiController(
        ISpeakingService speaking,
        ISpeechAuthorizationTokenService speechTokens,
        ILanguageContext languageContext,
        IOptions<SpeakingOptions> options)
    {
        _speaking = speaking;
        _speechTokens = speechTokens;
        _languageContext = languageContext;
        _options = options.Value;
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

        if (SpeakingAvatarCatalog.IsTutor(avatar.Id) && !_options.GenericTutorEnabled)
        {
            return NotFound();
        }

        if (!SpeakingAvatarCatalog.TryParseCefr(request.CefrLevel, out var cefrLevel))
        {
            return BadRequest(new { error = "CEFR level must be A1, A2, B1, B2, or C1." });
        }

        var created = await _speaking.CreateSessionAsync(
            User.GetUserId(),
            avatar,
            cefrLevel,
            request.QuizId,
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
            request.PracticePromptId,
            cancellationToken);
        return Ok(turn);
    }

    [HttpPut("sessions/{sessionId:guid}/quiz")]
    public async Task<IActionResult> SelectQuiz(
        Guid sessionId,
        [FromBody] SelectSpeakingQuizRequest request,
        CancellationToken cancellationToken)
    {
        var activeQuiz = await _speaking.SelectQuizAsync(
            sessionId,
            User.GetUserId(),
            request.QuizId,
            cancellationToken);
        return Ok(new { activeQuiz });
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
            request.DrinkId,
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
