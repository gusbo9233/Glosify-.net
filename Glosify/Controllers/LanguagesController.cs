using Glosify.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[Authorize]
public class LanguagesController : Controller
{
    private readonly ILanguageContext _languageContext;

    public LanguagesController(ILanguageContext languageContext)
    {
        _languageContext = languageContext;
    }

    public IActionResult Index() => View();

    [HttpPost]
    public IActionResult Select(string language)
    {
        if (!_languageContext.TrySetLanguage(language))
        {
            return RedirectToAction(nameof(Index));
        }

        return RedirectToAction("Index", "Home");
    }

    [HttpPost]
    public IActionResult Clear()
    {
        _languageContext.Clear();
        return RedirectToAction(nameof(Index));
    }
}
