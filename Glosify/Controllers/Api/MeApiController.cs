using Glosify.Models.Api;
using Glosify.Services;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Glosify.Services.Ai;

namespace Glosify.Controllers.Api;

/// <summary>
/// Account-scoped info for the mobile app (profile chrome + AI credit balance).
/// </summary>
[Route("api/me")]
public class MeApiController : ApiControllerBase
{
    private readonly IAiCreditService _aiCreditService;

    public MeApiController(IAiCreditService aiCreditService)
    {
        _aiCreditService = aiCreditService;
    }

    [HttpGet]
    public async Task<ActionResult<MeDto>> Get(CancellationToken cancellationToken)
    {
        var account = await _aiCreditService.GetOrCreateAccountAsync(User.GetUserId(), cancellationToken);
        var email = User.FindFirstValue(ClaimTypes.Email) ?? User.Identity?.Name ?? "Signed in";
        return Ok(new MeDto(email, account.AvailableCredits));
    }
}
