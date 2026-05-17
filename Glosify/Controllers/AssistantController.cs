using System.Security.Claims;
using Glosify.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[Authorize]
[Route("Quiz/{quizId:guid}/Assistant")]
public class AssistantController : Controller
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
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var history = await _orchestrator.GetHistoryAsync(quizId, userId, cancellationToken);
        return Json(history);
    }

    [HttpPost("Send")]
    public async Task<IActionResult> Send(Guid quizId, [FromBody] SendMessageInput input, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(input?.Message))
        {
            return BadRequest(new { error = "Type a message first." });
        }

        try
        {
            var response = await _orchestrator.SendMessageAsync(quizId, userId, input.Message, cancellationToken);
            return Json(response);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Quiz not found"))
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex) when (ServiceWarmupMessage.IsDatabaseWarmupFailure(ex) || ServiceWarmupMessage.IsLlmWarmupFailure(ex))
        {
            _logger.LogWarning(ex, "Dependency warm-up interrupted assistant turn for quiz {QuizId}", quizId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ServiceWarmupMessage.Dependencies });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Assistant turn failed for quiz {QuizId}", quizId);
            var detail = $"{ex.GetType().Name}: {ex.Message}";
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = detail });
        }
    }

    [HttpPost("Apply/{messageId:guid}")]
    public async Task<IActionResult> Apply(Guid quizId, Guid messageId, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        try
        {
            var applied = await _orchestrator.ApplyPendingChangesAsync(messageId, userId, cancellationToken);
            return Json(new { applied });
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

    [HttpPost("Reject/{messageId:guid}")]
    public async Task<IActionResult> Reject(Guid quizId, Guid messageId, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

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
    }
}
