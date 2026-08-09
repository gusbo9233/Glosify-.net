using Glosify.Models.Api;
using Glosify.Models.Entities;
using Glosify.Services.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers.Api;

/// <summary>
/// Google sign-in for the mobile app, reusing the same Google OAuth client and
/// account-linking rules as the web AccountController (match by email, create on
/// first sign-in). The flow runs in the system browser:
///   app -> GET google/start?code_challenge=... -> Google -> GET google/callback ->
///   redirect to glosify://auth?code=... -> app POSTs the code and its verifier to
///   exchange -> bearer tokens.
/// Tokens never travel through the browser redirect; only a short-lived code does, and
/// that code is bound by PKCE to a secret the app never let out of its own process.
/// </summary>
[ApiController]
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[Route("api/auth/external")]
public class ExternalAuthApiController : ControllerBase
{
    private const string CallbackScheme = "glosify://auth";
    private const string ChallengePropertyKey = "glosify:pkce_challenge";

    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAuthenticationSchemeProvider _schemeProvider;
    private readonly IMobileAuthorizationCodeStore _codes;
    private readonly IExternalAccountService _externalAccounts;

    public ExternalAuthApiController(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        IAuthenticationSchemeProvider schemeProvider,
        IMobileAuthorizationCodeStore codes,
        IExternalAccountService externalAccounts)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _schemeProvider = schemeProvider;
        _codes = codes;
        _externalAccounts = externalAccounts;
    }

    [HttpGet("google/start")]
    public async Task<IActionResult> GoogleStart([FromQuery(Name = "code_challenge")] string? codeChallenge)
    {
        if (await _schemeProvider.GetSchemeAsync("Google") == null)
        {
            return NotFound("Google login is not configured.");
        }

        if (!Pkce.IsValidChallenge(codeChallenge))
        {
            return BadRequest("A valid S256 code_challenge is required.");
        }

        var redirectUrl = Url.Action(nameof(GoogleCallback));
        var properties = _signInManager.ConfigureExternalAuthenticationProperties("Google", redirectUrl);
        // Carried through Google in the external auth cookie, which is encrypted with the
        // app's data protection keys, so the challenge cannot be swapped in transit.
        properties.Items[ChallengePropertyKey] = codeChallenge;
        return Challenge(properties, "Google");
    }

    [HttpGet("google/callback")]
    public async Task<IActionResult> GoogleCallback()
    {
        var info = await _signInManager.GetExternalLoginInfoAsync();
        if (info == null)
        {
            return AppRedirect("error=Google sign-in failed.");
        }

        var resolution = await _externalAccounts.ResolveOrCreateAsync(info);
        if (!resolution.Succeeded)
        {
            return AppRedirect($"error={Uri.EscapeDataString(resolution.ErrorMessage ?? "Google sign-in failed.")}");
        }
        var user = resolution.User!;

        // The challenge was pinned to this sign-in attempt in google/start and came back
        // inside the encrypted external cookie.
        string? codeChallenge = null;
        info.AuthenticationProperties?.Items.TryGetValue(ChallengePropertyKey, out codeChallenge);
        if (!Pkce.IsValidChallenge(codeChallenge))
        {
            return AppRedirect("error=Sign-in could not be verified. Please try again.");
        }

        // Clear the temporary external cookie used to carry the Google principal.
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

        var code = _codes.Create(user.Id, codeChallenge!);
        return AppRedirect($"code={Uri.EscapeDataString(code)}");
    }

    [HttpPost("exchange")]
    public async Task<IActionResult> Exchange([FromBody] ExchangeCodeRequest request)
    {
        Response.Headers.CacheControl = "no-store";
        if (!_codes.TryRedeem(request.Code, request.CodeVerifier, out var userId))
        {
            return Unauthorized("Invalid or expired code.");
        }

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return Unauthorized("Invalid or expired code.");
        }

        // The bearer-token handler writes the AccessTokenResponse JSON to the response.
        _signInManager.AuthenticationScheme = IdentityConstants.BearerScheme;
        await _signInManager.SignInAsync(user, isPersistent: false);
        return new EmptyResult();
    }

    private RedirectResult AppRedirect(string query) => Redirect($"{CallbackScheme}?{query}");
}
