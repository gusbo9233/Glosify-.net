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

    [HttpGet("~/Assistant/History")]
    public async Task<IActionResult> GlobalHistory(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var history = await _orchestrator.GetGlobalHistoryAsync(userId, cancellationToken);
        return Ok(history);
    }

    [HttpGet("~/Assistant/Chats")]
    public async Task<IActionResult> Chats(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var chats = await _orchestrator.ListChatsAsync(userId, cancellationToken);
        return Ok(new { chats });
    }

    [HttpPost("~/Assistant/Chats")]
    public async Task<IActionResult> CreateChat([FromBody] ChatMutationInput? input, CancellationToken cancellationToken)
    {
        try
        {
            var userId = User.GetUserId();
            var chat = await _orchestrator.CreateChatAsync(userId, input?.ContextQuizId, cancellationToken);
            return Ok(chat);
        }
        catch (QuizNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPatch("~/Assistant/Chats/{threadId:guid}")]
    public async Task<IActionResult> UpdateChat(Guid threadId, [FromBody] ChatMutationInput input, CancellationToken cancellationToken)
    {
        try
        {
            var userId = User.GetUserId();
            var chat = await _orchestrator.UpdateChatAsync(
                threadId,
                userId,
                input.Title,
                input.ContextQuizId,
                input.UpdateContext,
                cancellationToken);
            return Ok(chat);
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

    [HttpDelete("~/Assistant/Chats/{threadId:guid}")]
    public async Task<IActionResult> DeleteChat(Guid threadId, CancellationToken cancellationToken)
    {
        try
        {
            var userId = User.GetUserId();
            await _orchestrator.DeleteChatAsync(threadId, userId, cancellationToken);
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

    [HttpGet("~/Assistant/Chats/{threadId:guid}/History")]
    public async Task<IActionResult> ChatHistory(Guid threadId, CancellationToken cancellationToken)
    {
        try
        {
            var userId = User.GetUserId();
            var history = await _orchestrator.GetChatHistoryAsync(threadId, userId, cancellationToken);
            return Ok(history);
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

    [HttpPost("~/Assistant/Chats/{threadId:guid}/Send")]
    public async Task<IActionResult> ChatSend(Guid threadId, [FromBody] SendMessageInput input, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input?.Message))
        {
            return BadRequest(new { error = "Type a message first." });
        }

        try
        {
            var userId = User.GetUserId();
            var response = await _orchestrator.SendChatMessageAsync(
                threadId,
                userId,
                input.Message,
                input.ContextQuizId,
                input.FocusedWordId,
                input.Model,
                input.DocumentContext is null
                    ? null
                    : new AssistantDocumentContext(input.DocumentContext.DocumentId, input.DocumentContext.PageNumber),
                cancellationToken);

            return Ok(response);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (QuizNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InsufficientAiCreditsException ex)
        {
            return StatusCode(StatusCodes.Status402PaymentRequired, new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex) when (ServiceWarmupMessage.IsDatabaseWarmupFailure(ex) || ServiceWarmupMessage.IsLlmWarmupFailure(ex))
        {
            _logger.LogWarning(ex, "Dependency warm-up interrupted assistant turn for chat {ThreadId}", threadId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ServiceWarmupMessage.Dependencies });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Assistant turn failed for chat {ThreadId}", threadId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = $"{ex.GetType().Name}: {ex.Message}" });
        }
    }

    [HttpPost("~/Assistant/Send")]
    public async Task<IActionResult> GlobalSend([FromBody] SendMessageInput input, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input?.Message))
        {
            return BadRequest(new { error = "Type a message first." });
        }

        try
        {
            var userId = User.GetUserId();
            var response = await _orchestrator.SendGlobalMessageAsync(
                userId,
                input.Message,
                input.Model,
                input.DocumentContext is null
                    ? null
                    : new AssistantDocumentContext(input.DocumentContext.DocumentId, input.DocumentContext.PageNumber),
                cancellationToken);

            return Ok(response);
        }
        catch (Exception ex) when (ServiceWarmupMessage.IsLlmWarmupFailure(ex))
        {
            _logger.LogWarning(ex, "Dependency warm-up interrupted global assistant turn");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ServiceWarmupMessage.LlmAssistant });
        }
        catch (InsufficientAiCreditsException ex)
        {
            return StatusCode(StatusCodes.Status402PaymentRequired, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Global assistant turn failed");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = $"{ex.GetType().Name}: {ex.Message}" });
        }
    }

    [HttpPost("~/Assistant/Apply/{messageId:guid}")]
    public async Task<IActionResult> GlobalApply(Guid messageId, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();

        try
        {
            var applied = await _orchestrator.ApplyGlobalPendingChangesAsync(messageId, userId, cancellationToken);
            return Ok(applied);
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

    [HttpPost("~/Assistant/Reject/{messageId:guid}")]
    public async Task<IActionResult> GlobalReject(Guid messageId, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();

        try
        {
            await _orchestrator.RejectGlobalPendingChangesAsync(messageId, userId, cancellationToken);
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

    [HttpPost("~/Assistant/Reset")]
    public async Task<IActionResult> GlobalReset(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        await _orchestrator.ResetGlobalSessionAsync(userId, cancellationToken);
        return Ok();
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
        catch (InsufficientAiCreditsException ex)
        {
            return StatusCode(StatusCodes.Status402PaymentRequired, new { error = ex.Message });
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
            return Ok(applied);
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
        public Guid? ContextQuizId { get; set; }
        public string? FocusedWordId { get; set; }
        public string? Model { get; set; }
        public DocumentContextInput? DocumentContext { get; set; }
    }

    public sealed class ChatMutationInput
    {
        public string? Title { get; set; }
        public Guid? ContextQuizId { get; set; }
        public bool UpdateContext { get; set; }
    }

    public sealed class DocumentContextInput
    {
        public Guid DocumentId { get; set; }
        public int PageNumber { get; set; }
    }

}
