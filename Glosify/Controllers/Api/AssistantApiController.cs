using Glosify.Models.Api;
using Glosify.Filters;
using Glosify.Services;
using Microsoft.AspNetCore.Mvc;
using Glosify.Services.Ai.Assistant;

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
        var chat = await _orchestrator.CreateChatAsync(User.GetUserId(), input?.ContextQuizId, cancellationToken);
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
                cancellationToken);
            return Ok(response);
        }
        catch (InvalidOperationException ex)
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
        catch (InvalidOperationException ex)
        {
            return NotFound(ex.Message);
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
}
