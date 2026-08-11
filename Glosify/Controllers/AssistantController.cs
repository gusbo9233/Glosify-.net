using Glosify.Extensions;
using Glosify.Filters;
using Glosify.Infrastructure.Api;
using Glosify.Models.Api;
using Glosify.Models.CustomQuizzes;
using Glosify.Services;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Quizzes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[Authorize]
[ApiController]
[Route("Quiz/{quizId:guid}/Assistant")]
[AiServiceExceptionFilter]
public class AssistantController : ControllerBase
{
    private readonly IAssistantOrchestrator _orchestrator;

    public AssistantController(IAssistantOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
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
        var userId = User.GetUserId();
        var chat = await _orchestrator.CreateChatAsync(
            userId,
            input?.ContextQuizId,
            cancellationToken,
            input?.ContextTranscriptId,
            input?.ContextBookDocumentId);
        return Ok(chat);
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
                cancellationToken,
                input.ContextTranscriptId,
                input.ContextBookDocumentId);
            return Ok(chat);
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
                input.CustomQuizId,
                cancellationToken,
                input.TranscriptId,
                input.BookDocumentId,
                input.TranscriptContext is null
                    ? null
                    : new AssistantTranscriptPageContext(input.TranscriptContext.Page, input.TranscriptContext.Stream));

            return Ok(response);
        }
        catch (InvalidOperationException ex) when (
            ex is not InsufficientAiCreditsException
            and not MonthlyAiBudgetExceededException
            and not AssistantTurnInProgressException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("~/Assistant/Send")]
    public async Task<IActionResult> GlobalSend([FromBody] SendMessageInput input, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input?.Message))
        {
            return BadRequest(new { error = "Type a message first." });
        }

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

    [HttpPost("~/Assistant/Apply/{messageId:guid}")]
    public async Task<IActionResult> GlobalApply(Guid messageId, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();

        try
        {
            var applied = await _orchestrator.ApplyGlobalPendingChangesAsync(messageId, userId, cancellationToken);
            return Ok(applied);
        }
        catch (InvalidOperationException ex) when (
            ex is not CollectionParentNotFoundException
            and not CollectionNameConflictException
            and not QuizCollectionNotFoundException)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (CustomQuizValidationException ex)
        {
            return BadRequest(new { error = string.Join(" ", ex.Errors), errors = ex.Errors });
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

    [HttpPut("~/Assistant/Turns/{turnId:guid}/Feedback")]
    public async Task<IActionResult> SaveFeedback(
        Guid turnId,
        [FromBody] AssistantFeedbackInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            var feedback = await _orchestrator.SaveFeedbackAsync(
                turnId,
                User.GetUserId(),
                input.Rating,
                input.ReasonCodes,
                input.Comment,
                cancellationToken);
            return Ok(feedback);
        }
        catch (AssistantFeedbackValidationException ex)
        {
            return GlosifyProblemDetails.Result(
                HttpContext,
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.BadRequest,
                ex.Message);
        }
        catch (InvalidOperationException)
        {
            return FeedbackTurnNotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return FeedbackTurnNotFound();
        }
    }

    [HttpDelete("~/Assistant/Turns/{turnId:guid}/Feedback")]
    public async Task<IActionResult> DeleteFeedback(Guid turnId, CancellationToken cancellationToken)
    {
        try
        {
            await _orchestrator.DeleteFeedbackAsync(turnId, User.GetUserId(), cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException)
        {
            return FeedbackTurnNotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return FeedbackTurnNotFound();
        }
    }

    [HttpPut("~/Assistant/Turns/{turnId:guid}/ClientMetrics")]
    public async Task<IActionResult> SaveClientMetrics(
        Guid turnId,
        [FromBody] AssistantClientMetricsInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            await _orchestrator.RecordClientDurationAsync(
                turnId,
                User.GetUserId(),
                input.ClientDurationMs,
                cancellationToken);
            return NoContent();
        }
        catch (AssistantFeedbackValidationException ex)
        {
            return GlosifyProblemDetails.Result(
                HttpContext,
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.BadRequest,
                ex.Message);
        }
        catch (InvalidOperationException)
        {
            return FeedbackTurnNotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return FeedbackTurnNotFound();
        }
    }

    private ObjectResult FeedbackTurnNotFound() => GlosifyProblemDetails.Result(
        HttpContext,
        StatusCodes.Status404NotFound,
        ApiErrorCodes.NotFound,
        "Assistant turn not found.");

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
        catch (InvalidOperationException ex) when (
            ex is not InsufficientAiCreditsException
            and not MonthlyAiBudgetExceededException
            and not AssistantTurnInProgressException)
        {
            return BadRequest(new { error = ex.Message });
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
        catch (InvalidOperationException ex) when (
            ex is not CollectionParentNotFoundException
            and not CollectionNameConflictException
            and not QuizCollectionNotFoundException)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (CustomQuizValidationException ex)
        {
            return BadRequest(new { error = string.Join(" ", ex.Errors), errors = ex.Errors });
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
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }
}
