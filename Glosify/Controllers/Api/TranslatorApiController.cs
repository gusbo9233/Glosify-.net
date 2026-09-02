using Glosify.Extensions;
using Glosify.Filters;
using Glosify.Models.Api;
using Glosify.Services.Translator;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers.Api;

[Route("api/translator")]
[AiServiceExceptionFilter]
public sealed class TranslatorApiController : ApiControllerBase
{
    private readonly ITextTranslationService _translator;

    public TranslatorApiController(ITextTranslationService translator)
    {
        _translator = translator;
    }

    [HttpGet("catalog")]
    public ActionResult<TranslatorCatalogDto> Catalog()
    {
        NoStore();
        return Ok(_translator.GetCatalog());
    }

    [HttpPost("translate")]
    [RequirePaidServices]
    public async Task<ActionResult<TranslateTextResponse>> Translate(
        [FromBody] TranslateTextRequest request,
        CancellationToken cancellationToken)
    {
        NoStore();
        var result = await _translator.TranslateAsync(
            User.GetUserId(),
            request.SourceText,
            request.SourceLanguage,
            request.TargetLanguage,
            request.Preferences,
            cancellationToken);
        return Ok(new TranslateTextResponse(
            result.TranslationOperationId,
            result.SourceText,
            result.SourceLanguage,
            result.DetectedSourceLanguage,
            result.TargetLanguage,
            result.TranslatedText,
            result.AvailableCredits));
    }

    [HttpPost("saved-translations")]
    public async Task<ActionResult<SavedTranslationCreatedDto>> Save(
        [FromBody] SaveTranslationRequest request,
        CancellationToken cancellationToken)
    {
        NoStore();
        var saved = await _translator.SaveAsync(
            User.GetUserId(),
            request.SessionId,
            request.RequestId,
            request.TranslationOperationId,
            request.LanguageCode,
            request.SourceText,
            request.TranslatedText,
            request.SourceLanguage,
            request.DetectedSourceLanguage,
            request.TargetLanguage,
            request.Preferences,
            cancellationToken);
        var historyUrl = Url.Action(
            nameof(TranslationsController.Details),
            "Translations",
            new { id = saved.SessionId }) ?? $"/Translations/{saved.SessionId}";
        var response = new SavedTranslationCreatedDto(
            saved.Id,
            saved.SessionId,
            saved.LanguageCode,
            saved.CreatedAt,
            historyUrl);
        return Created(historyUrl, response);
    }

    private void NoStore() => Response.Headers.CacheControl = "no-store";
}
