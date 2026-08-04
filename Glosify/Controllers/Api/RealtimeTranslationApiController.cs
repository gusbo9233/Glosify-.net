using Glosify.Filters;
using Glosify.Models.Api;
using Glosify.Services.RealtimeTranslation;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers.Api;

[Route("api/realtime-translation")]
[AiServiceExceptionFilter]
public sealed class RealtimeTranslationApiController : ApiControllerBase
{
    private readonly IRealtimeTranslationService _translation;

    public RealtimeTranslationApiController(IRealtimeTranslationService translation)
    {
        _translation = translation;
    }

    [HttpGet("catalog")]
    public async Task<ActionResult<RealtimeTranslationCatalog>> Catalog(CancellationToken cancellationToken)
    {
        NoStore();
        return Ok(await _translation.GetCatalogAsync(User.GetUserId(), cancellationToken));
    }

    [HttpPost("sessions")]
    public async Task<ActionResult<RealtimeTranslationSessionCreated>> CreateSession(
        [FromBody] CreateRealtimeTranslationSessionRequest request,
        CancellationToken cancellationToken)
    {
        NoStore();
        var created = await _translation.CreateSessionAsync(
            User.GetUserId(),
            request.TargetLanguage,
            request.SaveTranscript,
            request.TranscriptId,
            cancellationToken);
        return Ok(created);
    }

    [HttpPost("sessions/{sessionId:guid}/minutes/{minuteIndex:int}/reserve")]
    public async Task<ActionResult<RealtimeTranslationMinuteResult>> ReserveMinute(
        Guid sessionId,
        int minuteIndex,
        CancellationToken cancellationToken)
    {
        NoStore();
        return Ok(await _translation.ReserveMinuteAsync(
            User.GetUserId(),
            sessionId,
            minuteIndex,
            cancellationToken));
    }

    [HttpPost("sessions/{sessionId:guid}/minutes/{minuteIndex:int}/begin")]
    public async Task<ActionResult<RealtimeTranslationMinuteResult>> BeginMinute(
        Guid sessionId,
        int minuteIndex,
        CancellationToken cancellationToken)
    {
        NoStore();
        return Ok(await _translation.BeginMinuteAsync(
            User.GetUserId(),
            sessionId,
            minuteIndex,
            cancellationToken));
    }

    [HttpPost("sessions/{sessionId:guid}/heartbeat")]
    public async Task<ActionResult<RealtimeTranslationSessionStatus>> Heartbeat(
        Guid sessionId,
        [FromBody] RealtimeTranslationHeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        NoStore();
        if (request.FirstCaptionLatencyMs is >= 0 and <= 300_000)
        {
            RealtimeTranslationTelemetry.FirstCaptionLatency.Record(request.FirstCaptionLatencyMs.Value);
        }
        if (request.Reconnected)
        {
            RealtimeTranslationTelemetry.Reconnects.Add(1);
        }
        return Ok(await _translation.HeartbeatAsync(User.GetUserId(), sessionId, cancellationToken));
    }

    [HttpDelete("sessions/{sessionId:guid}")]
    public async Task<IActionResult> EndSession(Guid sessionId, CancellationToken cancellationToken)
    {
        NoStore();
        await _translation.EndSessionAsync(User.GetUserId(), sessionId, cancellationToken);
        return NoContent();
    }

    private void NoStore() => Response.Headers.CacheControl = "no-store";
}
