using Glosify.Extensions;
using Glosify.Filters;
using Glosify.Infrastructure.Api;
using Glosify.Models.QuizImports;
using Glosify.Services.Ai;
using Glosify.Services.Language;
using Glosify.Services.Quizzes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[ApiController]
[Authorize]
[Route("Quiz")]
public sealed class QuizImportController : ControllerBase
{
    private const int ImportRequestLimit = 96 * 1024;

    private readonly IQuizJsonImportService _imports;
    private readonly IQuizJsonImportRepairService _repair;
    private readonly ILanguageContext _languageContext;

    public QuizImportController(
        IQuizJsonImportService imports,
        IQuizJsonImportRepairService repair,
        ILanguageContext languageContext)
    {
        _imports = imports;
        _repair = repair;
        _languageContext = languageContext;
    }

    [HttpPost("PreviewJsonImport")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(ImportRequestLimit)]
    public async Task<ActionResult<QuizJsonImportPreview>> PreviewJsonImport(
        [FromForm] QuizJsonImportRequest request,
        CancellationToken cancellationToken)
    {
        var targetLanguage = _languageContext.CurrentLanguage;
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            return MissingLanguage();
        }
        try
        {
            return Ok(await _imports.PreviewAsync(
                request.Json,
                targetLanguage,
                request.ParentCollectionId,
                User.GetUserId(),
                cancellationToken));
        }
        catch (QuizJsonImportValidationException exception)
        {
            return GlosifyProblemDetails.ValidationResult(
                HttpContext,
                exception.Errors,
                exception.CanonicalJson);
        }
    }

    [HttpPost("RepairJsonImportWithAi")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(ImportRequestLimit)]
    [AiServiceExceptionFilter]
    public async Task<ActionResult<QuizJsonImportPreview>> RepairJsonImportWithAi(
        [FromForm] QuizJsonImportRequest request,
        CancellationToken cancellationToken)
    {
        var targetLanguage = _languageContext.CurrentLanguage;
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            return MissingLanguage();
        }
        try
        {
            return Ok(await _repair.RepairAsync(
                request.Json,
                targetLanguage,
                request.ParentCollectionId,
                User.GetUserId(),
                cancellationToken));
        }
        catch (QuizJsonImportAiUnprocessableException exception) when (exception.Errors is not null)
        {
            return GlosifyProblemDetails.ValidationResult(
                HttpContext,
                exception.Errors,
                exception.CanonicalJson,
                StatusCodes.Status422UnprocessableEntity,
                ApiErrorCodes.UnprocessableEntity);
        }
    }

    [HttpPost("ApplyJsonImport")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(ImportRequestLimit)]
    public async Task<ActionResult<QuizJsonImportApplyResponse>> ApplyJsonImport(
        [FromForm] QuizJsonImportRequest request,
        CancellationToken cancellationToken)
    {
        var targetLanguage = _languageContext.CurrentLanguage;
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            return MissingLanguage();
        }
        QuizJsonImportResult result;
        try
        {
            result = await _imports.ApplyAsync(
                request.Json,
                targetLanguage,
                request.ParentCollectionId,
                User.GetUserId(),
                cancellationToken);
        }
        catch (QuizJsonImportValidationException exception)
        {
            return GlosifyProblemDetails.ValidationResult(
                HttpContext,
                exception.Errors,
                exception.CanonicalJson);
        }

        var redirectUrl = request.ParentCollectionId.HasValue
            ? Url.Action(nameof(QuizController.Collection), "Quiz", new { id = request.ParentCollectionId.Value })
            : Url.Action(nameof(QuizController.Index), "Quiz");
        return Ok(new QuizJsonImportApplyResponse(
            result.CollectionCount,
            result.QuizCount,
            result.WordCount,
            result.SentenceCount,
            redirectUrl ?? "/Quizzes"));
    }

    private ObjectResult MissingLanguage() => GlosifyProblemDetails.ValidationResult(
        HttpContext,
        new Dictionary<string, string[]>
        {
            ["$.target_language"] = ["Choose a quiz language before importing."],
        });
}
