using Glosify.Services.Speech;
using System.Security.Claims;
using System.ComponentModel.DataAnnotations;
using Glosify.Filters;
using Glosify.Services.Ai;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Glosify.Controllers.Api;

[ApiController]
[Authorize]
[Route("api/tts")]
public sealed class TtsApiController : ControllerBase
{
    private readonly IAiCreditService _credits;
    private readonly ITextToSpeechService _tts;
    private readonly SpeechOptions _options;
    private readonly ILogger<TtsApiController> _logger;

    public TtsApiController(
        ITextToSpeechService tts,
        IOptions<SpeechOptions> options,
        ILogger<TtsApiController> logger,
        IAiCreditService credits)
    {
        _credits = credits;
        _tts = tts;
        _options = options.Value;
        _logger = logger;
    }

    [HttpGet("voices")]
    public async Task<IActionResult> Voices([FromQuery] string lang, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(new { configured = _tts.IsConfigured, creditsPerRequest = _options.CreditsPerRequest, maxTextLength = _options.MaxTextLength, voices = await _tts.GetVoicesAsync(lang, cancellationToken) });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Speech voice catalog could not be loaded.");
            return Glosify.Infrastructure.Api.GlosifyProblemDetails.Result(
                HttpContext, StatusCodes.Status502BadGateway, "speech_voices_unavailable",
                "Azure voices could not be loaded. Try again or choose browser speech.");
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePaidServices]
    [AiServiceExceptionFilter]
    public async Task<IActionResult> Synthesize(
        [FromBody] SpeechPlaybackRequest request,
        CancellationToken cancellationToken)
    {
        var text = request.Text;
        var lang = request.Lang;
        var voice = request.Voice;
        var quality = request.Quality;
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
        if (request.MaxCredits < _options.CreditsPerRequest)
            return Conflict("Speech pricing changed. Open speech settings again to review the price.");
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(lang))
        {
            return BadRequest("text and lang are required.");
        }

        if (text.Length > _options.MaxTextLength)
        {
            return BadRequest($"text exceeds max length of {_options.MaxTextLength}.");
        }

        if (!_tts.IsConfigured)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Speech service not configured.");
        }

        Guid? reservationId = null;
        Stream? audio = null;
        var settled = false;
        try
        {
            var preferHighDefinition = string.Equals(quality, "hd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(quality, "hd-supported-v2", StringComparison.OrdinalIgnoreCase);
            reservationId = await _credits.ReserveSpeechAsync(userId, _options.CreditsPerRequest, cancellationToken);
            audio = await _tts.GetOrSynthesizeAsync(
                text,
                lang,
                preferHighDefinition,
                voice,
                cancellationToken);
            await _credits.CommitSpeechAsync(reservationId.Value, CancellationToken.None);
            settled = true;
            Response.Headers.CacheControl = "no-store";
            return File(audio, "audio/mpeg");
        }
        catch (NotSupportedException ex)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, ex.Message);
        }
        catch (Exception ex) when (ex is PaidServicesBudgetExhaustedException or InsufficientAiCreditsException or OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Keep user-supplied speech text and language out of logs.
            _logger.LogError(ex, "TTS synthesis failed.");
            return StatusCode(StatusCodes.Status502BadGateway, "Speech synthesis failed.");
        }
        finally
        {
            if (!settled)
            {
                if (audio is not null) await audio.DisposeAsync();
                if (reservationId.HasValue)
                    await _credits.ReleaseAsync(reservationId.Value, CancellationToken.None);
            }
        }
    }
}

public sealed class SpeechPlaybackRequest
{
    [Required]
    public string Text { get; set; } = string.Empty;
    [Required, StringLength(64)]
    public string Lang { get; set; } = string.Empty;
    [StringLength(128)]
    public string? Voice { get; set; }
    [StringLength(32)]
    public string? Quality { get; set; }
    [Range(1, int.MaxValue)]
    public int MaxCredits { get; set; }
}
