using Glosify.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers
{
    [Authorize]
    public class LanguagesController : Controller
    {
        private readonly ILanguageContext _languageContext;

        public LanguagesController(ILanguageContext languageContext)
        {
            _languageContext = languageContext;
        }

        public IActionResult Index()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Select(string language)
        {
            if (!_languageContext.TrySetLanguage(language))
            {
                return RedirectToAction(nameof(Index));
            }

            return RedirectToAction("Index", "Home");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Clear()
        {
            _languageContext.Clear();
            return RedirectToAction(nameof(Index));
        }
    }
}
