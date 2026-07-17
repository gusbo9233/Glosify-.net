using Glosify.Services.Language;
using Glosify.Services.Speaking;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[Authorize]
public sealed class SpeakingController : Controller
{
    private readonly ILanguageContext _languageContext;

    public SpeakingController(ILanguageContext languageContext)
    {
        _languageContext = languageContext;
    }

    [HttpGet("/Speaking")]
    public IActionResult Index()
    {
        var language = _languageContext.CurrentLanguage;
        if (language is null)
        {
            return RedirectToAction("Index", "Languages");
        }

        ViewData["HideAssistantPanel"] = true;
        return View(SpeakingAvatarCatalog.CreatePageViewModel(language));
    }
}
