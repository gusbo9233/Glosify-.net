using Glosify.Controllers.Api;
using Glosify.Extensions;
using Glosify.Services.Ai.Assistant.Runtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Glosify.Controllers;

[ApiController, Authorize, Route("Assistant/Tasks")]
public sealed class AssistantTasksController(AssistantTaskStore tasks, IOptions<AssistantRuntimeOptions> options) : ControllerBase
{
    [HttpGet("capabilities")]
    public IActionResult Capabilities() => Ok(new { enabled = options.Value.Enabled && options.Value.WebEnabled });
    [HttpPost("chats/{threadId:guid}")]
    public Task<IActionResult> Start(Guid threadId, [FromBody] AssistantTaskStartInput input, CancellationToken ct) =>
        AssistantTaskHttp.Run(this, async () =>
        {
            var task = await tasks.StartAsync(threadId, User.GetUserId(), input, ct);
            return Accepted($"/Assistant/Tasks/{task.Id}", task);
        });
    [HttpGet("chats/{threadId:guid}")]
    public Task<IActionResult> Active(Guid threadId, CancellationToken ct) =>
        AssistantTaskHttp.Run(this, async () => Ok(await tasks.ActiveAsync(threadId, User.GetUserId(), ct)));
    [HttpGet("{id:guid}")]
    public Task<IActionResult> Status(Guid id, CancellationToken ct) =>
        AssistantTaskHttp.Run(this, async () => Ok(await tasks.ViewAsync(id, User.GetUserId(), ct)));
    [HttpPost("{id:guid}/{command:regex(^(steer|cancel|resume|approve)$)}")]
    public Task<IActionResult> Command(Guid id, string command, [FromBody] AssistantTaskCommand input, CancellationToken ct) =>
        AssistantTaskHttp.Run(this, async () => Ok(await tasks.CommandAsync(id, User.GetUserId(), command, input, ct)));
}

internal static class AssistantTaskHttp
{
    internal static async Task<IActionResult> Run(ControllerBase controller, Func<Task<IActionResult>> operation)
    {
        try { return await operation(); }
        catch (KeyNotFoundException) { return controller.NotFound("Task or chat not found."); }
        catch (AssistantTaskConflictException ex) { return controller.Conflict(ex.Message); }
        catch (ArgumentException ex) { return controller.BadRequest(ex.Message); }
    }
}

[Route("api/assistant/tasks")]
public sealed class AssistantTasksApiController(AssistantTaskStore tasks) : ApiControllerBase
{
    [HttpPost("chats/{threadId:guid}")]
    public Task<IActionResult> Start(Guid threadId, [FromBody] AssistantTaskStartInput input, CancellationToken ct) =>
        AssistantTaskHttp.Run(this, async () =>
        {
            var task = await tasks.StartAsync(threadId, User.GetUserId(), input, ct);
            return Accepted($"/api/assistant/tasks/{task.Id}", task);
        });
    [HttpGet("chats/{threadId:guid}")]
    public Task<IActionResult> Active(Guid threadId, CancellationToken ct) =>
        AssistantTaskHttp.Run(this, async () => Ok(await tasks.ActiveAsync(threadId, User.GetUserId(), ct)));
    [HttpGet("{id:guid}")]
    public Task<IActionResult> Status(Guid id, CancellationToken ct) =>
        AssistantTaskHttp.Run(this, async () => Ok(await tasks.ViewAsync(id, User.GetUserId(), ct)));
    [HttpPost("{id:guid}/{command:regex(^(steer|cancel|resume|approve)$)}")]
    public Task<IActionResult> Command(Guid id, string command, [FromBody] AssistantTaskCommand input, CancellationToken ct) =>
        AssistantTaskHttp.Run(this, async () => Ok(await tasks.CommandAsync(id, User.GetUserId(), command, input, ct)));
}
