using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Models.ViewModels;
using Glosify.Services.Auth;
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
    private readonly IExternalAccountService _externalAccounts;

    public AccountController(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        IAuthenticationSchemeProvider schemeProvider,
        IExternalAccountService externalAccounts)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _schemeProvider = schemeProvider;
        _externalAccounts = externalAccounts;
    }

    [HttpGet]
    public async Task<IActionResult> Login(string? returnUrl = null, string? externalLoginError = null)
    {
        if (User.Identity?.IsAuthenticated == true && string.IsNullOrWhiteSpace(externalLoginError))
        {
            return LocalRedirect(SafeLocalReturnUrl(returnUrl));
        }

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
            return LocalRedirect(SafeLocalReturnUrl(returnUrl));

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
        return RedirectToAction("Index", "Home");
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
            return LocalRedirect(SafeLocalReturnUrl(returnUrl));

        var resolution = await _externalAccounts.ResolveOrCreateAsync(info);
        if (!resolution.Succeeded)
        {
            ModelState.AddModelError(string.Empty, resolution.ErrorMessage ?? "External login failed.");
            await SetLoginViewDataAsync(returnUrl);
            return View("Login", new LoginViewModel());
        }

        await _signInManager.SignInAsync(resolution.User!, isPersistent: false);
        return LocalRedirect(SafeLocalReturnUrl(returnUrl));
    }

    [HttpGet]
    public IActionResult ForgotPassword()
    {
        return View();
    }

    private async Task SetLoginViewDataAsync(string? returnUrl)
    {
        ViewData["ReturnUrl"] = Url.IsLocalUrl(returnUrl) ? returnUrl : null;
        ViewData["GoogleLoginEnabled"] = await IsExternalLoginProviderConfigured("Google");
        ViewData["MicrosoftLoginEnabled"] = await IsExternalLoginProviderConfigured("Microsoft");
    }

    private async Task<bool> IsExternalLoginProviderConfigured(string provider)
    {
        var schemes = await _schemeProvider.GetAllSchemesAsync();
        return schemes.Any(scheme => string.Equals(scheme.Name, provider, StringComparison.OrdinalIgnoreCase));
    }

    private string SafeLocalReturnUrl(string? returnUrl) =>
        Url.IsLocalUrl(returnUrl) ? returnUrl! : "/";
}
