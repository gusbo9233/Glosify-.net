using Glosify.Extensions;
using Glosify.Filters;
using Glosify.Infrastructure.Api;
using Glosify.Models.Api;
using Glosify.Models.CustomQuizzes;
using Glosify.Services;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Quizzes;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers.Api;

/// <summary>
/// Bearer-token assistant endpoints for the mobile app. Wraps the same orchestrator
/// as the cookie-authenticated AssistantController used by the web assistant panel.
/// </summary>
[Route("api/assistant")]
[AiServiceExceptionFilter]
public class AssistantApiController : ApiControllerBase
{
    private readonly IAssistantOrchestrator _orchestrator;

    public AssistantApiController(IAssistantOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    [HttpGet("chats")]
    public async Task<IActionResult> Chats(CancellationToken cancellationToken)
    {
        var chats = await _orchestrator.ListChatsAsync(User.GetUserId(), cancellationToken);
        return Ok(chats);
    }

    [HttpPost("chats")]
    public async Task<IActionResult> CreateChat([FromBody] AssistantChatInput? input, CancellationToken cancellationToken)
    {
        var chat = await _orchestrator.CreateChatAsync(
            User.GetUserId(),
            input?.ContextQuizId,
            cancellationToken,
            input?.ContextTranscriptId,
            input?.ContextBookDocumentId);
        return Ok(chat);
    }

    [HttpDelete("chats/{threadId:guid}")]
    public async Task<IActionResult> DeleteChat(Guid threadId, CancellationToken cancellationToken)
    {
        try
        {
            await _orchestrator.DeleteChatAsync(threadId, User.GetUserId(), cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ex.Message);
        }
    }

    [HttpGet("chats/{threadId:guid}/history")]
    public async Task<IActionResult> History(Guid threadId, CancellationToken cancellationToken)
    {
        try
        {
            var history = await _orchestrator.GetChatHistoryAsync(threadId, User.GetUserId(), cancellationToken);
            return Ok(history);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ex.Message);
        }
    }

    [HttpPost("chats/{threadId:guid}/send")]
    public async Task<IActionResult> Send(Guid threadId, [FromBody] AssistantSendInput input, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input?.Message))
        {
            return BadRequest("Type a message first.");
        }

        try
        {
            var response = await _orchestrator.SendChatMessageAsync(
                threadId,
                User.GetUserId(),
                input.Message,
                input.ContextQuizId,
                input.FocusedWordId,
                input.Model,
                input.DocumentId is Guid documentId
                    ? new AssistantDocumentContext(documentId, input.PageNumber ?? 1)
                    : null,
                input.CustomQuizId,
                cancellationToken,
                input.TranscriptId,
                input.BookDocumentId);
            return Ok(response);
        }
        catch (InvalidOperationException ex) when (
            ex is not InsufficientAiCreditsException
            and not MonthlyAiBudgetExceededException
            and not AssistantTurnInProgressException
            and not GenerativeAiDependencyUnavailableException
            and not GenerativeAiUpstreamException)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("apply/{messageId:guid}")]
    public async Task<IActionResult> Apply(Guid messageId, CancellationToken cancellationToken)
    {
        try
        {
            var applied = await _orchestrator.ApplyGlobalPendingChangesAsync(messageId, User.GetUserId(), cancellationToken);
            return Ok(applied);
        }
        catch (InvalidOperationException ex) when (
            ex is not CollectionParentNotFoundException
            and not CollectionNameConflictException
            and not QuizCollectionNotFoundException)
        {
            return NotFound(ex.Message);
        }
        catch (CustomQuizValidationException ex)
        {
            return BadRequest(new { error = string.Join(" ", ex.Errors), errors = ex.Errors });
        }
    }

    [HttpPost("reject/{messageId:guid}")]
    public async Task<IActionResult> Reject(Guid messageId, CancellationToken cancellationToken)
    {
        try
        {
            await _orchestrator.RejectGlobalPendingChangesAsync(messageId, User.GetUserId(), cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ex.Message);
        }
    }

    [HttpPut("turns/{turnId:guid}/feedback")]
    public async Task<IActionResult> SaveFeedback(
        Guid turnId,
        [FromBody] AssistantFeedbackInput input,
        CancellationToken cancellationToken)
    {
        return Ok(await _orchestrator.SaveFeedbackAsync(
            turnId,
            User.GetUserId(),
            input.Rating,
            input.ReasonCodes,
            input.Comment,
            cancellationToken));
    }

    [HttpDelete("turns/{turnId:guid}/feedback")]
    public async Task<IActionResult> DeleteFeedback(Guid turnId, CancellationToken cancellationToken)
    {
        await _orchestrator.DeleteFeedbackAsync(turnId, User.GetUserId(), cancellationToken);
        return NoContent();
    }

    [HttpPut("turns/{turnId:guid}/client-metrics")]
    public async Task<IActionResult> SaveClientMetrics(
        Guid turnId,
        [FromBody] AssistantClientMetricsInput input,
        CancellationToken cancellationToken)
    {
        await _orchestrator.RecordClientDurationAsync(
            turnId,
            User.GetUserId(),
            input.ClientDurationMs,
            cancellationToken);
        return NoContent();
    }
}
