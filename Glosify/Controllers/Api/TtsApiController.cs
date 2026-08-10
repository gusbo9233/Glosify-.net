using Glosify.Services.Speech;
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
    private readonly ITextToSpeechService _tts;
    private readonly SpeechOptions _options;
    private readonly ILogger<TtsApiController> _logger;

    public TtsApiController(
        ITextToSpeechService tts,
        IOptions<SpeechOptions> options,
        ILogger<TtsApiController> logger)
    {
        _tts = tts;
        _options = options.Value;
        _logger = logger;
    }

    [HttpGet]
    [RequirePaidServices]
    [AiServiceExceptionFilter]
    public async Task<IActionResult> Get(
        [FromQuery] string text,
        [FromQuery] string lang,
        [FromQuery] string? quality,
        [FromQuery] string? voice,
        CancellationToken cancellationToken)
    {
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
            // Signals the browser to use its SpeechSynthesis fallback.
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Speech service not configured.");
        }

        try
        {
            var preferHighDefinition = string.Equals(quality, "hd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(quality, "hd-supported-v2", StringComparison.OrdinalIgnoreCase);
            var stream = await _tts.GetOrSynthesizeAsync(
                text,
                lang,
                preferHighDefinition,
                voice,
                cancellationToken);
            Response.Headers.CacheControl = "private, max-age=604800";
            return File(stream, "audio/mpeg");
        }
        catch (NotSupportedException ex)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, ex.Message);
        }
        catch (PaidServicesBudgetExhaustedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The requested language came from the query string. Keeping it out of the
            // log prevents control characters from creating misleading log entries.
            _logger.LogError(ex, "TTS synthesis failed.");
            return StatusCode(StatusCodes.Status502BadGateway, "Speech synthesis failed.");
        }
    }
}
