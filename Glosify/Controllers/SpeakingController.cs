using Glosify.Extensions;
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
    private readonly ISpeakingQuizReader _quizReader;

    public SpeakingController(
        ILanguageContext languageContext,
        IOptions<SpeakingOptions> options,
        ISpeakingQuizReader quizReader)
    {
        _languageContext = languageContext;
        _options = options.Value;
        _quizReader = quizReader;
    }

    [HttpGet("/Speaking")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var language = _languageContext.CurrentLanguage;
        if (language is null)
        {
            return RedirectToAction("Index", "Languages");
        }

        ViewData["HideAssistantPanel"] = true;
        var quizzes = _options.GenericTutorEnabled
            ? await _quizReader.ListAsync(
                User.GetUserId(),
                language,
                cancellationToken)
            : [];
        return View(SpeakingAvatarCatalog.CreatePageViewModel(
            language,
            quizzes,
            _options.InteractiveBartenderEnabled,
            _options.GenericTutorEnabled));
    }
}
