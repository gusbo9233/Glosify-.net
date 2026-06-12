using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers.Api;

/// <summary>
/// Base for JSON API controllers used by the mobile app. Authenticates with Identity
/// bearer tokens (issued by /api/auth/login) instead of the web app's cookies, and opts
/// out of the globally registered antiforgery filter since token auth is not CSRF-prone.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = "Identity.Bearer")]
[IgnoreAntiforgeryToken]
public abstract class ApiControllerBase : ControllerBase
{
}
