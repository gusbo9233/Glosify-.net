using Glosify.Services.Ai;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers.Api;

/// <summary>
/// Reports the shared paid-service state to both cookie-authenticated web pages and
/// bearer-authenticated first-party clients such as the Chrome extension. This endpoint
/// intentionally does not inherit <see cref="ApiControllerBase"/>, whose bearer-only
/// contract would prevent active web calls from polling for budget closure.
/// </summary>
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
