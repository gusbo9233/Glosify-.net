using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Glosify.Controllers.Api;
using Glosify.Extensions;
using Glosify.Filters;
using Glosify.Services.Ai.Assistant.Runtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

/// <summary>Cookie-authenticated run endpoints for the web assistant panel.</summary>
[ApiController]
[Authorize]
[Route("Assistant/Runs")]
[AiServiceExceptionFilter]
public sealed class AssistantRunsController(IAssistantRunService runs) : ControllerBase
{
    [HttpPost("chats/{threadId:guid}")]
    public Task<IActionResult> Start(Guid threadId, [FromBody] AssistantRunStartInput input, CancellationToken cancellationToken) =>
        AssistantRunHttp.Run(this, async () =>
        {
            var run = await runs.StartAsync(threadId, User.GetUserId(), input, cancellationToken);
            return Accepted($"/Assistant/Runs/{run.Id}", run);
        });

    [HttpGet("chats/{threadId:guid}")]
    public Task<IActionResult> Latest(Guid threadId, CancellationToken cancellationToken) =>
        AssistantRunHttp.Run(this, async () => Ok(await runs.LatestAsync(threadId, User.GetUserId(), cancellationToken)));

    [HttpGet("{id:guid}")]
    public Task<IActionResult> Status(Guid id, CancellationToken cancellationToken) =>
        AssistantRunHttp.Run(this, async () => Ok(await runs.ViewAsync(id, User.GetUserId(), cancellationToken)));

    [HttpGet("{id:guid}/events")]
    public Task<IActionResult> Events(Guid id, CancellationToken cancellationToken) =>
        AssistantRunHttp.Events(this, runs, id, cancellationToken);

    [HttpPost("{id:guid}/{command:regex(^(steer|cancel|resume|approve|reject|answer)$)}")]
    public Task<IActionResult> Command(Guid id, string command, [FromBody] AssistantRunCommand input, CancellationToken cancellationToken) =>
        AssistantRunHttp.Run(this, async () => Ok(await runs.CommandAsync(id, User.GetUserId(), command, input, cancellationToken)));

    [HttpPost("{id:guid}/undo")]
    public Task<IActionResult> Undo(Guid id, CancellationToken cancellationToken) =>
        AssistantRunHttp.Undo(this, runs, id, cancellationToken);
}

/// <summary>Bearer-authenticated run endpoints for the mobile app.</summary>
[Route("api/assistant/runs")]
[AiServiceExceptionFilter]
public sealed class AssistantRunsApiController(IAssistantRunService runs) : ApiControllerBase
{
    [HttpPost("chats/{threadId:guid}")]
    public Task<IActionResult> Start(Guid threadId, [FromBody] AssistantRunStartInput input, CancellationToken cancellationToken) =>
        AssistantRunHttp.Run(this, async () =>
        {
            var run = await runs.StartAsync(threadId, User.GetUserId(), input, cancellationToken);
            return Accepted($"/api/assistant/runs/{run.Id}", run);
        });

    [HttpGet("chats/{threadId:guid}")]
    public Task<IActionResult> Latest(Guid threadId, CancellationToken cancellationToken) =>
        AssistantRunHttp.Run(this, async () => Ok(await runs.LatestAsync(threadId, User.GetUserId(), cancellationToken)));

    [HttpGet("{id:guid}")]
    public Task<IActionResult> Status(Guid id, CancellationToken cancellationToken) =>
        AssistantRunHttp.Run(this, async () => Ok(await runs.ViewAsync(id, User.GetUserId(), cancellationToken)));

    [HttpGet("{id:guid}/events")]
    public Task<IActionResult> Events(Guid id, CancellationToken cancellationToken) =>
        AssistantRunHttp.Events(this, runs, id, cancellationToken);

    [HttpPost("{id:guid}/{command:regex(^(steer|cancel|resume|approve|reject|answer)$)}")]
    public Task<IActionResult> Command(Guid id, string command, [FromBody] AssistantRunCommand input, CancellationToken cancellationToken) =>
        AssistantRunHttp.Run(this, async () => Ok(await runs.CommandAsync(id, User.GetUserId(), command, input, cancellationToken)));

    [HttpPost("{id:guid}/undo")]
    public Task<IActionResult> Undo(Guid id, CancellationToken cancellationToken) =>
        AssistantRunHttp.Undo(this, runs, id, cancellationToken);
}

internal static class AssistantRunHttp
{
    /// <summary>How long one event stream stays open. Clients reconnect, resuming from Last-Event-ID.</summary>
    private static readonly TimeSpan StreamLifetime = TimeSpan.FromSeconds(45);

    internal static async Task<IActionResult> Run(ControllerBase controller, Func<Task<IActionResult>> operation)
    {
        try
        {
            return await operation();
        }
        catch (KeyNotFoundException ex)
        {
            return controller.NotFound(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return controller.BadRequest(ex.Message);
        }
    }

    internal static Task<IActionResult> Undo(ControllerBase controller, IAssistantRunService runs, Guid id, CancellationToken cancellationToken) =>
        Run(controller, async () =>
        {
            var userId = controller.User.GetUserId();
            var result = await runs.UndoAsync(id, userId, cancellationToken);
            return controller.Ok(new { result.Undone, result.Kept, run = await runs.ViewAsync(id, userId, cancellationToken) });
        });

    /// <summary>
    /// Streams the run's view as server-sent events whenever its revision changes. The event id is
    /// the revision, so a reconnecting client receives nothing it has already seen.
    /// </summary>
    internal static Task<IActionResult> Events(ControllerBase controller, IAssistantRunService runs, Guid id, CancellationToken cancellationToken) =>
        Run(controller, async () =>
        {
            long? seen = long.TryParse(controller.Request.Headers["Last-Event-ID"], out var revision) ? revision : null;
            var views = await runs.WatchAsync(id, controller.User.GetUserId(), seen, StreamLifetime, cancellationToken);
            return new ResultAdapter(TypedResults.ServerSentEvents(Items(views, cancellationToken)));
        });

    private static async IAsyncEnumerable<SseItem<AssistantRunView>> Items(
        IAsyncEnumerable<AssistantRunView> views,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var view in views.WithCancellation(cancellationToken))
        {
            yield return new SseItem<AssistantRunView>(view, "run") { EventId = view.Revision.ToString() };
        }
    }

    /// <summary>Lets a controller action return the framework's server-sent events result.</summary>
    private sealed class ResultAdapter(IResult result) : IActionResult
    {
        public Task ExecuteResultAsync(ActionContext context) => result.ExecuteAsync(context.HttpContext);
    }
}
