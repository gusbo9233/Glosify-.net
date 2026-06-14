using Glosify.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[Authorize]
[ApiController]
[Route("Quiz/{quizId:guid}/Assistant")]
public class AssistantController : ControllerBase
{
    private readonly IAssistantOrchestrator _orchestrator;
    private readonly ILogger<AssistantController> _logger;

    public AssistantController(
        IAssistantOrchestrator orchestrator,
        ILogger<AssistantController> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    [HttpGet("History")]
    public async Task<IActionResult> History(Guid quizId, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var history = await _orchestrator.GetHistoryAsync(quizId, userId, cancellationToken);
        return Ok(history);
    }

    [HttpPost("Send")]
    public async Task<IActionResult> Send(Guid quizId, [FromBody] SendMessageInput input, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();

        if (string.IsNullOrWhiteSpace(input?.Message))
        {
            return BadRequest(new { error = "Type a message first." });
        }

        try
        {
            var response = await _orchestrator.SendMessageAsync(
                quizId,
                userId,
                input.Message,
                input.FocusedWordId,
                input.Model,
                input.DocumentContext is null
                    ? null
                    : new AssistantDocumentContext(input.DocumentContext.DocumentId, input.DocumentContext.PageNumber),
                cancellationToken);
            return Ok(response);
        }
        catch (QuizNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex) when (ServiceWarmupMessage.IsDatabaseWarmupFailure(ex) || ServiceWarmupMessage.IsLlmWarmupFailure(ex))
        {
            _logger.LogWarning(ex, "Dependency warm-up interrupted assistant turn for quiz {QuizId}", quizId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ServiceWarmupMessage.Dependencies });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Assistant turn failed for quiz {QuizId}", quizId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = $"{ex.GetType().Name}: {ex.Message}" });
        }
    }

    [HttpPost("Apply/{messageId:guid}")]
    public async Task<IActionResult> Apply(Guid quizId, Guid messageId, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();

        try
        {
            var applied = await _orchestrator.ApplyPendingChangesAsync(messageId, userId, cancellationToken);
            return Ok(new { applied });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (QuizNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost("Reject/{messageId:guid}")]
    public async Task<IActionResult> Reject(Guid quizId, Guid messageId, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();

        try
        {
            await _orchestrator.RejectPendingChangesAsync(messageId, userId, cancellationToken);
            return Ok();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    public sealed class SendMessageInput
    {
        public string Message { get; set; } = string.Empty;
        public string? FocusedWordId { get; set; }
        public string? Model { get; set; }
        public DocumentContextInput? DocumentContext { get; set; }
    }

    public sealed class DocumentContextInput
    {
        public Guid DocumentId { get; set; }
        public int PageNumber { get; set; }
    }
}
