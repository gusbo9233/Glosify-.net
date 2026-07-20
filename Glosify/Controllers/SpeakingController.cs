using Glosify.Services.Language;
using Glosify.Services.Speaking;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Glosify.Controllers;

[Authorize]
public sealed class SpeakingController : Controller
{
    private readonly ILanguageContext _languageContext;
    private readonly SpeakingOptions _options;

    public SpeakingController(
        ILanguageContext languageContext,
        IOptions<SpeakingOptions> options)
    {
        _languageContext = languageContext;
        _options = options.Value;
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
        return View(SpeakingAvatarCatalog.CreatePageViewModel(
            language,
            _options.InteractiveBartenderEnabled));
    }
}
