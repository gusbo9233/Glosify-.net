using Glosify.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Glosify.Controllers;

[Authorize(AuthenticationSchemes = "Identity.Application")]
[Route("extension")]
public sealed class ExtensionAuthController : Controller
{
    private readonly IExtensionAuthorizationCodeStore _codes;
    private readonly ExtensionAuthOptions _options;

    public ExtensionAuthController(
        IExtensionAuthorizationCodeStore codes,
        IOptions<ExtensionAuthOptions> options)
    {
        _codes = codes;
        _options = options.Value;
    }

    [HttpGet("connect")]
    public IActionResult Connect(
        [FromQuery(Name = "redirect_uri")] string? redirectUri,
        [FromQuery] string? state,
        [FromQuery(Name = "code_challenge")] string? codeChallenge,
        [FromQuery(Name = "code_challenge_method")] string? codeChallengeMethod)
    {
        Response.Headers.CacheControl = "no-store";
        if (!_options.IsAllowedRedirectUri(redirectUri))
        {
            return BadRequest("This Chrome extension redirect URI is not configured in Glosify.");
        }
        if (string.IsNullOrWhiteSpace(state) || state.Length > 256)
        {
            return BadRequest("A valid OAuth state value is required.");
        }
        if (!string.Equals(codeChallengeMethod, "S256", StringComparison.Ordinal))
        {
            return BadRequest("Only the S256 PKCE method is supported.");
        }
        if (string.IsNullOrWhiteSpace(codeChallenge))
        {
            return BadRequest("A valid S256 PKCE code challenge is required.");
        }

        string code;
        try
        {
            code = _codes.Create(User.GetUserId(), redirectUri!, codeChallenge);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(exception.Message);
        }

        var callback = QueryHelpers.AddQueryString(redirectUri!, new Dictionary<string, string?>
        {
            ["code"] = code,
            ["state"] = state,
        });
        return Redirect(callback);
    }
}
