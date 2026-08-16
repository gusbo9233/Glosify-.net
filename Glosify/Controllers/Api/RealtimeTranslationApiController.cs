using Glosify.Extensions;
using Glosify.Filters;
using Glosify.Models.Api;
using Glosify.Services.Language;
using Glosify.Services.RealtimeTranslation;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers.Api;

[Route("api/realtime-translation")]
[AiServiceExceptionFilter]
public sealed class RealtimeTranslationApiController : ApiControllerBase
{
    private readonly IRealtimeTranslationService _translation;
    private readonly IQuizLanguagePreferenceService _languagePreferences;

    public RealtimeTranslationApiController(
        IRealtimeTranslationService translation,
        IQuizLanguagePreferenceService languagePreferences)
    {
        _translation = translation;
        _languagePreferences = languagePreferences;
    }

    [HttpPut("quiz-language")]
    public async Task<ActionResult<RealtimeTranslationSelectedQuizLanguage>> SetQuizLanguage(
        [FromBody] SetRealtimeTranslationQuizLanguageRequest request,
        CancellationToken cancellationToken)
    {
        NoStore();
        if (QuizLanguageCatalog.Find(request.Code) is not { IsLanguageLearning: true })
        {
            throw new RealtimeTranslationValidationException(
                "Choose a supported learning language.");
        }
        var selected = await _languagePreferences.SetSelectedAsync(
            User.GetUserId(),
            request.Code,
            cancellationToken);
        return Ok(new RealtimeTranslationSelectedQuizLanguage(selected.Code, selected.Name));
    }

    [HttpGet("catalog")]
    public async Task<ActionResult<RealtimeTranslationCatalog>> Catalog(CancellationToken cancellationToken)
    {
        NoStore();
        await EnsureLanguageLearningModeAsync(cancellationToken);
        return Ok(await _translation.GetCatalogAsync(User.GetUserId(), cancellationToken));
    }

    [HttpPost("sessions")]
    [RequirePaidServices]
    public async Task<ActionResult<RealtimeTranslationSessionCreated>> CreateSession(
        [FromBody] CreateRealtimeTranslationSessionRequest request,
        CancellationToken cancellationToken)
    {
        NoStore();
        await EnsureLanguageLearningModeAsync(cancellationToken);
        var created = await _translation.CreateSessionAsync(
            User.GetUserId(),
            request.TargetLanguage,
            request.SaveTranscript,
            request.TranscriptId,
            request.TranslationMode,
            request.SourceLanguage,
            cancellationToken);
        return Ok(created);
    }

    [HttpPost("sessions/{sessionId:guid}/minutes/{minuteIndex:int}/reserve")]
    [RequirePaidServices]
    public async Task<ActionResult<RealtimeTranslationMinuteResult>> ReserveMinute(
        Guid sessionId,
        int minuteIndex,
        CancellationToken cancellationToken)
    {
        NoStore();
        await EnsureLanguageLearningModeAsync(cancellationToken);
        return Ok(await _translation.ReserveMinuteAsync(
            User.GetUserId(),
            sessionId,
            minuteIndex,
            cancellationToken));
    }

    [HttpPost("sessions/{sessionId:guid}/minutes/{minuteIndex:int}/begin")]
    [RequirePaidServices]
    public async Task<ActionResult<RealtimeTranslationMinuteResult>> BeginMinute(
        Guid sessionId,
        int minuteIndex,
        CancellationToken cancellationToken)
    {
        NoStore();
        await EnsureLanguageLearningModeAsync(cancellationToken);
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
        await EnsureLanguageLearningModeAsync(cancellationToken);
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

    private async Task EnsureLanguageLearningModeAsync(CancellationToken cancellationToken)
    {
        var selected = await _languagePreferences.GetSelectedAsync(User.GetUserId(), cancellationToken);
        if (selected is { IsLanguageLearning: false })
        {
            throw new RealtimeTranslationValidationException(
                "Realtime translation is not available in Freestyle mode.");
        }
    }
}
