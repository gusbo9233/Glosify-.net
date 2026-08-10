using Glosify.Services.Ai;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers.Api;

[Route("api/service-status/paid-features")]
[ApiController]
[Authorize(AuthenticationSchemes = "Identity.Application,Identity.Bearer")]
[IgnoreAntiforgeryToken]
public sealed class PaidServiceStatusApiController(IPaidServiceGate gate) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PaidServiceStatus>> Get(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await gate.GetStatusAsync(cancellationToken));
    }
}
