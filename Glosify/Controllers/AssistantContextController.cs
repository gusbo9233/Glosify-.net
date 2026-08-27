using Glosify.Extensions;
using Glosify.Models.ViewModels;
using Glosify.Services.Ai.Assistant;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[Authorize]
[ApiController]
[Route("Assistant/ContextOptions")]
public sealed class AssistantContextController : ControllerBase
{
    private readonly AssistantContextOptionsProvider _options;

    public AssistantContextController(AssistantContextOptionsProvider options)
    {
        _options = options;
    }

    [HttpGet]
    public async Task<ActionResult<AssistantContextOptions>> Get(
        CancellationToken cancellationToken) =>
        Ok(await _options.GetAsync(User.GetUserId(), cancellationToken));
}
