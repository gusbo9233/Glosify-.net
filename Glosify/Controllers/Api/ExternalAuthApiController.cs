using Glosify.Models.Api;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Glosify.Controllers.Api;

/// <summary>
/// Google sign-in for the mobile app, reusing the same Google OAuth client and
/// account-linking rules as the web AccountController (match by email, create on
/// first sign-in). The flow runs in the system browser:
///   app -> GET google/start -> Google -> GET google/callback -> redirect to
///   glosify://auth?code=... -> app POSTs the one-time code to exchange -> bearer tokens.
/// Tokens never travel through the browser redirect; only a short-lived code does.
/// </summary>
[ApiController]
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[Route("api/auth/external")]
public class ExternalAuthApiController : ControllerBase
{
    private const string CallbackScheme = "glosify://auth";
    private const string CodeCachePrefix = "mobile-auth-code:";
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(2);

    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAuthenticationSchemeProvider _schemeProvider;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ExternalAuthApiController> _logger;

    public ExternalAuthApiController(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        IAuthenticationSchemeProvider schemeProvider,
        IMemoryCache cache,
        ILogger<ExternalAuthApiController> logger)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _schemeProvider = schemeProvider;
        _cache = cache;
        _logger = logger;
    }

    [HttpGet("google/start")]
    public async Task<IActionResult> GoogleStart()
    {
        if (await _schemeProvider.GetSchemeAsync("Google") == null)
        {
            return NotFound("Google login is not configured.");
        }

        var redirectUrl = Url.Action(nameof(GoogleCallback));
        var properties = _signInManager.ConfigureExternalAuthenticationProperties("Google", redirectUrl);
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

        var user = await _userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
        if (user == null)
        {
            var email = GetExternalLoginEmail(info);
            if (string.IsNullOrWhiteSpace(email))
            {
                return AppRedirect("error=Google did not provide an email address.");
            }

            user = await _userManager.FindByEmailAsync(email);
            if (user != null)
            {
                var addLoginResult = await _userManager.AddLoginAsync(user, info);
                if (!addLoginResult.Succeeded)
                {
                    _logger.LogWarning("Could not link Google login: {Errors}",
                        string.Join("; ", addLoginResult.Errors.Select(e => e.Description)));
                    return AppRedirect("error=Could not link the Google account.");
                }
            }
            else
            {
                user = new ApplicationUser { UserName = email, Email = email };
                var createResult = await _userManager.CreateAsync(user);
                if (!createResult.Succeeded)
                {
                    _logger.LogWarning("Could not create user from Google login: {Errors}",
                        string.Join("; ", createResult.Errors.Select(e => e.Description)));
                    return AppRedirect("error=Could not create an account.");
                }
                await _userManager.AddLoginAsync(user, info);
            }
        }

        // Clear the temporary external cookie used to carry the Google principal.
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

        var code = $"{Guid.NewGuid():N}{Guid.NewGuid():N}";
        _cache.Set(CodeCachePrefix + code, user.Id, CodeLifetime);
        return AppRedirect($"code={Uri.EscapeDataString(code)}");
    }

    [HttpPost("exchange")]
    public async Task<IActionResult> Exchange([FromBody] ExchangeCodeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Code) ||
            !_cache.TryGetValue(CodeCachePrefix + request.Code, out string? userId) ||
            string.IsNullOrEmpty(userId))
        {
            return Unauthorized("Invalid or expired code.");
        }
        _cache.Remove(CodeCachePrefix + request.Code);

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

    // Only real email claims; preferred_username/upn are not verified email addresses.
    private static string GetExternalLoginEmail(ExternalLoginInfo info)
    {
        return info.Principal.FindFirst(ClaimTypes.Email)?.Value
            ?? info.Principal.FindFirst("email")?.Value
            ?? string.Empty;
    }
}
