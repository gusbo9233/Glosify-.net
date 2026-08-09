using Glosify.Models.Api;
using Glosify.Models.Entities;
using Glosify.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers.Api;

[ApiController]
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[Route("api/extension-auth")]
public sealed class ExtensionAuthApiController : ControllerBase
{
    private readonly IExtensionAuthorizationCodeStore _codes;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;

    public ExtensionAuthApiController(
        IExtensionAuthorizationCodeStore codes,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager)
    {
        _codes = codes;
        _userManager = userManager;
        _signInManager = signInManager;
    }

    [HttpPost("exchange")]
    public async Task<IActionResult> Exchange([FromBody] ExtensionExchangeCodeRequest request)
    {
        Response.Headers.CacheControl = "no-store";
        if (!_codes.TryRedeem(
                request.Code,
                request.RedirectUri,
                request.CodeVerifier,
                out var userId))
        {
            return Unauthorized(new { error = "Invalid or expired extension authorization code." });
        }

        var user = await _userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return Unauthorized(new { error = "Invalid or expired extension authorization code." });
        }

        // Identity's bearer handler writes the standard accessToken/expiresIn/
        // refreshToken JSON contract used by /api/auth/refresh.
        _signInManager.AuthenticationScheme = IdentityConstants.BearerScheme;
        await _signInManager.SignInAsync(user, isPersistent: false);
        return new EmptyResult();
    }
}
