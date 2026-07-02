using Glosify.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[AllowAnonymous]
public class AccountController : Controller
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAuthenticationSchemeProvider _schemeProvider;

    public AccountController(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        IAuthenticationSchemeProvider schemeProvider)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _schemeProvider = schemeProvider;
    }

    [HttpGet]
    public async Task<IActionResult> Login(string? returnUrl = null, string? externalLoginError = null)
    {
        if (!string.IsNullOrWhiteSpace(externalLoginError))
        {
            var externalLoginMessage = string.Equals(externalLoginError, "Google", StringComparison.OrdinalIgnoreCase)
                ? "Google login failed. Check the local Google OAuth client ID and client secret, then try again."
                : $"{externalLoginError} login failed. Check the local OAuth client ID and client secret, then try again.";

            ModelState.AddModelError(
                string.Empty,
                externalLoginMessage);
        }

        await SetLoginViewDataAsync(returnUrl);
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> Login(LoginViewModel model, string? returnUrl = null)
    {
        await SetLoginViewDataAsync(returnUrl);
        if (!ModelState.IsValid)
            return View(model);

        var result = await _signInManager.PasswordSignInAsync(model.Email, model.Password, model.RememberMe, lockoutOnFailure: true);

        if (result.Succeeded)
            return LocalRedirect(returnUrl ?? "/");

        if (result.IsLockedOut)
        {
            ModelState.AddModelError(string.Empty, "Account locked. Please try again later.");
            return View(model);
        }

        ModelState.AddModelError(string.Empty, "Invalid email or password.");
        return View(model);
    }

    [HttpGet]
    public IActionResult Register()
    {
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> Register(RegisterViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        var user = new ApplicationUser { UserName = model.Email, Email = model.Email };
        var result = await _userManager.CreateAsync(user, model.Password);

        if (result.Succeeded)
        {
            await _signInManager.SignInAsync(user, isPersistent: false);
            return RedirectToAction("Index", "Home");
        }

        foreach (var error in result.Errors)
            ModelState.AddModelError(string.Empty, error.Description);

        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> Logout()
    {
        await _signInManager.SignOutAsync();
        return RedirectToAction("Login", "Account");
    }

    [HttpPost]
    public async Task<IActionResult> ExternalLogin(string provider, string? returnUrl = null)
    {
        if (!await IsExternalLoginProviderConfigured(provider))
        {
            ModelState.AddModelError(string.Empty, $"{provider} login is not configured.");
            await SetLoginViewDataAsync(returnUrl);
            return View("Login", new LoginViewModel());
        }

        var redirectUrl = Url.Action("ExternalLoginCallback", "Account", new { returnUrl });
        var properties = _signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
        return Challenge(properties, provider);
    }

    [HttpGet]
    public async Task<IActionResult> ExternalLoginCallback(string? returnUrl = null)
    {
        var info = await _signInManager.GetExternalLoginInfoAsync();
        if (info == null)
            return RedirectToAction("Login");

        var result = await _signInManager.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, isPersistent: false);
        if (result.Succeeded)
            return LocalRedirect(returnUrl ?? "/");

        // First time — create the user
        var email = GetExternalLoginEmail(info);
        if (string.IsNullOrWhiteSpace(email))
        {
            ModelState.AddModelError(string.Empty, $"{info.ProviderDisplayName ?? info.LoginProvider} did not provide an email address.");
            await SetLoginViewDataAsync(returnUrl);
            return View("Login", new LoginViewModel());
        }

        var existingUser = await _userManager.FindByEmailAsync(email);
        if (existingUser != null)
        {
            // Only Google guarantees the email in its claims is verified. Other providers
            // (notably Microsoft work/school tenants) let tenant admins put arbitrary
            // addresses in the email claims, so auto-linking them to an existing account
            // would allow account takeover.
            if (!string.Equals(info.LoginProvider, "Google", StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError(
                    string.Empty,
                    "An account with this email already exists. Sign in with the method you originally used.");
                await SetLoginViewDataAsync(returnUrl);
                return View("Login", new LoginViewModel());
            }

            var addLoginResult = await _userManager.AddLoginAsync(existingUser, info);
            if (addLoginResult.Succeeded)
            {
                await _signInManager.SignInAsync(existingUser, isPersistent: false);
                return LocalRedirect(returnUrl ?? "/");
            }

            foreach (var error in addLoginResult.Errors)
                ModelState.AddModelError(string.Empty, error.Description);

            await SetLoginViewDataAsync(returnUrl);
            return View("Login", new LoginViewModel());
        }

        var user = new ApplicationUser { UserName = email, Email = email };
        var createResult = await _userManager.CreateAsync(user);
        if (createResult.Succeeded)
        {
            await _userManager.AddLoginAsync(user, info);
            await _signInManager.SignInAsync(user, isPersistent: false);
            return LocalRedirect(returnUrl ?? "/");
        }

        foreach (var error in createResult.Errors)
            ModelState.AddModelError(string.Empty, error.Description);

        await SetLoginViewDataAsync(returnUrl);
        return View("Login", new LoginViewModel());
    }

    [HttpGet]
    public IActionResult ForgotPassword()
    {
        return View();
    }

    private async Task SetLoginViewDataAsync(string? returnUrl)
    {
        ViewData["ReturnUrl"] = returnUrl;
        ViewData["GoogleLoginEnabled"] = await IsExternalLoginProviderConfigured("Google");
        ViewData["MicrosoftLoginEnabled"] = await IsExternalLoginProviderConfigured("Microsoft");
    }

    private async Task<bool> IsExternalLoginProviderConfigured(string provider)
    {
        var schemes = await _schemeProvider.GetAllSchemesAsync();
        return schemes.Any(scheme => string.Equals(scheme.Name, provider, StringComparison.OrdinalIgnoreCase));
    }

    // Only real email claims. preferred_username/upn are not email addresses and are
    // attacker-controlled in multi-tenant Microsoft sign-in, so they must never be
    // used to match or create accounts.
    private static string GetExternalLoginEmail(ExternalLoginInfo info)
    {
        return info.Principal.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
            ?? info.Principal.FindFirst("email")?.Value
            ?? string.Empty;
    }
}
