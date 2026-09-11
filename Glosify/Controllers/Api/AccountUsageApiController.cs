using Glosify.Extensions;
using Glosify.Services.Abuse;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers.Api;

[Route("api/account/usage")]
public sealed class AccountUsageApiController(ResourceQuotaService quotas) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ResourceUsageView>>> Get(CancellationToken ct) =>
        Ok(await quotas.GetUsageAsync(User.GetUserId(), ct));
}
