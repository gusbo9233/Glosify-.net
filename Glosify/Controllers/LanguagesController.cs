using Glosify.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Glosify.Services.Language;

namespace Glosify.Controllers;

[Authorize]
public class LanguagesController : Controller
{
    private readonly ILanguageContext _languageContext;
    private readonly IQuizLanguagePreferenceService _preferences;

    public LanguagesController(
        ILanguageContext languageContext,
        IQuizLanguagePreferenceService preferences)
    {
        _languageContext = languageContext;
        _preferences = preferences;
    }

    public IActionResult Index(string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = Url.IsLocalUrl(returnUrl) ? returnUrl : null;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Select(
        string language,
        string? returnUrl = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var selected = await _preferences.SetSelectedAsync(
                User.GetUserId(),
                language,
                cancellationToken);
            if (!_languageContext.TrySetLanguage(selected.Name))
            {
                return RedirectToAction(nameof(Index), new { returnUrl });
            }
        }
        catch (ArgumentException)
        {
            return RedirectToAction(nameof(Index), new { returnUrl });
        }

        return Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction("Index", "Home");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Clear(CancellationToken cancellationToken = default)
    {
        await _preferences.ClearAsync(User.GetUserId(), cancellationToken);
        _languageContext.Clear();
        return RedirectToAction(nameof(Index));
    }
}
