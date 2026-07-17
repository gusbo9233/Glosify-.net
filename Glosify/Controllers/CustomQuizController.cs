using Glosify.Models.CustomQuizzes;
using Glosify.Services.CustomQuizzes;
using Glosify.Services.Quizzes;
using Glosify.Services.Words;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[Authorize]
public sealed class CustomQuizController : Controller
{
    private readonly ICustomQuizService _customQuizzes;
    private readonly ICustomQuizTemplateCatalog _templates;
    private readonly IQuizService _quizzes;
    private readonly IWordService _words;

    public CustomQuizController(ICustomQuizService customQuizzes, ICustomQuizTemplateCatalog templates, IQuizService quizzes, IWordService words)
    {
        _customQuizzes = customQuizzes;
        _templates = templates;
        _quizzes = quizzes;
        _words = words;
    }

    [HttpGet("/Quizzes/{quizId:guid}/Custom/New")]
    public async Task<IActionResult> New(Guid quizId, CancellationToken cancellationToken)
    {
        var quiz = await _quizzes.GetQuizByIdAsync(quizId, User.GetUserId(), cancellationToken);
        if (quiz == null) return NotFound();
        var document = new CustomQuizDocumentV1
        {
            Blocks =
            [
                new() { Id = Guid.NewGuid().ToString("N"), Type = CustomQuizBlockTypes.SubmitButton, Order = 0, ColumnSpan = 6, GridColumn = 1, GridRow = 1, Text = "Check answers" },
                new() { Id = Guid.NewGuid().ToString("N"), Type = CustomQuizBlockTypes.FeedbackMessage, Order = 1, ColumnSpan = 6, GridColumn = 7, GridRow = 1 }
            ]
        };
        var words = await _words.GetWordsAsync(quizId, cancellationToken);
        return View("Editor", new CustomQuizEditorViewModel
        {
            Quiz = quiz,
            Words = words,
            Templates = _templates.Build(words),
            Editor = new CustomQuizEditorDto(null, quizId, "Custom quiz", document, false,
                ["Add at least one answer control."], string.Empty)
        });
    }

    [HttpGet("/CustomQuizzes/{id:guid}/Edit")]
    public async Task<IActionResult> Edit(Guid id, CancellationToken cancellationToken)
    {
        var editor = await _customQuizzes.GetForEditorAsync(id, User.GetUserId(), cancellationToken);
        if (editor == null) return NotFound();
        var quiz = await _quizzes.GetQuizByIdAsync(editor.QuizId, User.GetUserId(), cancellationToken);
        if (quiz == null) return NotFound();
        var words = await _words.GetWordsAsync(quiz.Id, cancellationToken);
        return View("Editor", new CustomQuizEditorViewModel
        {
            Quiz = quiz,
            Words = words,
            Templates = _templates.Build(words),
            Editor = editor
        });
    }

    [HttpPost("/CustomQuizzes")]
    public async Task<IActionResult> Create([FromBody] SaveCustomQuizRequest? request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || request?.Document is null)
        {
            return InvalidRequest(nameof(SaveCustomQuizRequest.Document), "A custom quiz document is required.");
        }

        try
        {
            return Ok(await _customQuizzes.CreateAsync(request, User.GetUserId(), cancellationToken));
        }
        catch (QuizNotFoundException) { return NotFound(); }
        catch (CustomQuizValidationException ex) { return BadRequest(new { errors = ex.Errors }); }
    }

    [HttpPut("/CustomQuizzes/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveCustomQuizRequest? request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || request?.Document is null)
        {
            return InvalidRequest(nameof(SaveCustomQuizRequest.Document), "A custom quiz document is required.");
        }

        try
        {
            var updated = await _customQuizzes.UpdateAsync(id, request, User.GetUserId(), cancellationToken);
            return updated == null ? NotFound() : Ok(updated);
        }
        catch (CustomQuizConcurrencyException) { return Conflict(new { error = "This custom quiz changed in another tab. Reload before saving again." }); }
        catch (CustomQuizValidationException ex) { return BadRequest(new { errors = ex.Errors }); }
    }

    [HttpDelete("/CustomQuizzes/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        await _customQuizzes.DeleteAsync(id, User.GetUserId(), cancellationToken) ? NoContent() : NotFound();

    [HttpGet("/CustomQuizzes/{id:guid}/Play")]
    public async Task<IActionResult> Play(Guid id, Guid? classroomId, CancellationToken cancellationToken)
    {
        var play = await _customQuizzes.GetForPlayAsync(id, User.GetUserId(), classroomId, cancellationToken);
        return play == null ? NotFound() : View(new CustomQuizPlayViewModel { Play = play });
    }

    [HttpPost("/CustomQuizzes/{id:guid}/Grade")]
    public async Task<IActionResult> Grade(Guid id, [FromBody] GradeCustomQuizRequest? request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid
            || request?.Answers is null
            || request.Answers.Any(answer =>
                answer is null
                || string.IsNullOrWhiteSpace(answer.BlockId)
                || answer.Values is null))
        {
            return InvalidRequest(nameof(GradeCustomQuizRequest.Answers), "A valid answers collection is required.");
        }

        var result = await _customQuizzes.GradeAsync(id, request, User.GetUserId(), cancellationToken);
        return result == null ? NotFound() : Ok(result);
    }

    private IActionResult InvalidRequest(string key, string message)
    {
        if (ModelState.IsValid)
        {
            ModelState.AddModelError(key, message);
        }

        return BadRequest(new ValidationProblemDetails(ModelState)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "One or more validation errors occurred.",
        });
    }
}
