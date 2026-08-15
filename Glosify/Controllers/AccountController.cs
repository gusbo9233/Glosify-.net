using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Models.ViewModels;
using Glosify.Services.Auth;
using Glosify.Localization;
using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Glosify.Controllers;

[AllowAnonymous]
public class AccountController : Controller
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAuthenticationSchemeProvider _schemeProvider;
    private readonly IExternalAccountService _externalAccounts;
    private readonly IStringLocalizer<UiText> _text;

    public AccountController(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        IAuthenticationSchemeProvider schemeProvider,
        IExternalAccountService externalAccounts)
        : this(
            signInManager,
            userManager,
            schemeProvider,
            externalAccounts,
            PassthroughStringLocalizer<UiText>.Instance)
    {
    }

    [ActivatorUtilitiesConstructor]
    public AccountController(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        IAuthenticationSchemeProvider schemeProvider,
        IExternalAccountService externalAccounts,
        IStringLocalizer<UiText> text)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _schemeProvider = schemeProvider;
        _externalAccounts = externalAccounts;
        _text = text;
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
            var externalLoginMessage = _text["Auth.ExternalProviderFailed", externalLoginError];

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
            ModelState.AddModelError(string.Empty, _text["Auth.AccountLocked"]);
            return View(model);
        }

        ModelState.AddModelError(string.Empty, _text["Auth.InvalidCredentials"]);
        return View(model);
    }

    [HttpGet]
    public IActionResult Register(string? returnUrl = null)
    {
        SetRegisterViewData(returnUrl);
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterViewModel model, string? returnUrl = null)
    {
        SetRegisterViewData(returnUrl);
        if (!ModelState.IsValid)
            return View(model);

        var user = new ApplicationUser
        {
            UserName = model.Email,
            Email = model.Email,
            DisplayCulture = CultureInfo.CurrentUICulture.Name,
        };
        var result = await _userManager.CreateAsync(user, model.Password);

        if (result.Succeeded)
        {
            await _signInManager.SignInAsync(user, isPersistent: false);
            return LocalRedirect(SafeLocalReturnUrl(returnUrl));
        }

        foreach (var error in result.Errors)
            ModelState.AddModelError(string.Empty, IdentityErrorMessage(error));

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
            ModelState.AddModelError(string.Empty, _text["Auth.ExternalNotConfigured", provider]);
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
            return RedirectToAction(nameof(Login), new { returnUrl });

        var result = await _signInManager.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, isPersistent: false);
        if (result.Succeeded)
            return LocalRedirect(SafeLocalReturnUrl(returnUrl));

        var resolution = await _externalAccounts.ResolveOrCreateAsync(info);
        if (!resolution.Succeeded)
        {
            ModelState.AddModelError(string.Empty, _text["Auth.ExternalFailed"]);
            await SetLoginViewDataAsync(returnUrl);
            return View("Login", new LoginViewModel());
        }

        if (string.IsNullOrWhiteSpace(resolution.User!.DisplayCulture))
        {
            resolution.User.DisplayCulture = CultureInfo.CurrentUICulture.Name;
            await _userManager.UpdateAsync(resolution.User);
        }
        await _signInManager.SignInAsync(resolution.User, isPersistent: false);
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

    private void SetRegisterViewData(string? returnUrl) =>
        ViewData["ReturnUrl"] = Url.IsLocalUrl(returnUrl) ? returnUrl : null;

    private async Task<bool> IsExternalLoginProviderConfigured(string provider)
    {
        var schemes = await _schemeProvider.GetAllSchemesAsync();
        return schemes.Any(scheme => string.Equals(scheme.Name, provider, StringComparison.OrdinalIgnoreCase));
    }

    private string SafeLocalReturnUrl(string? returnUrl) =>
        Url.IsLocalUrl(returnUrl) ? returnUrl! : "/";

    private string IdentityErrorMessage(IdentityError error)
    {
        var key = $"Identity.{error.Code}";
        var localized = _text[key];
        return localized.ResourceNotFound ? _text["Identity.DefaultError"] : localized;
    }
}
