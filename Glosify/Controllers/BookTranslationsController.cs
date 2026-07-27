using Glosify.Filters;
using Glosify.Models.Api;
using Glosify.Services;
using Glosify.Services.Books;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[Authorize]
[ApiController]
[Route("Books/{documentId:guid}")]
[AiServiceExceptionFilter]
public sealed class BookTranslationsController : ControllerBase
{
    private readonly IBookPageTranslationService _translations;

    public BookTranslationsController(IBookPageTranslationService translations)
    {
        _translations = translations;
    }

    [HttpPut("TranslationPreference")]
    public async Task<IActionResult> UpdatePreference(
        Guid documentId,
        [FromBody] UpdateBookTranslationPreferenceRequest? request,
        CancellationToken cancellationToken)
    {
        var targetLanguage = await _translations.UpdatePreferredLanguageAsync(
            documentId,
            User.GetUserId(),
            request?.TargetLanguage,
            cancellationToken);
        return Ok(new { targetLanguage });
    }

    [HttpPost("Pages/{pageNumber:int}/Translation")]
    public async Task<ActionResult<BookPageTranslationResult>> TranslatePage(
        Guid documentId,
        int pageNumber,
        [FromBody] TranslateBookPageRequest? request,
        CancellationToken cancellationToken)
    {
        var segments = request?.Segments?.Select(segment => new BookPageSourceSegment(
            segment.Index,
            segment.ParagraphIndex,
            segment.SourceText ?? string.Empty)).ToArray();
        var result = await _translations.TranslatePageAsync(
            documentId,
            pageNumber,
            User.GetUserId(),
            request?.TargetLanguage,
            segments,
            cancellationToken);
        return Ok(result);
    }
}
