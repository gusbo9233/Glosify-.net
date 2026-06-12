using Glosify.Services;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers.Api;

[Route("api/languages")]
public class LanguagesApiController : ApiControllerBase
{
    private readonly ILanguageContext _languageContext;

    public LanguagesApiController(ILanguageContext languageContext)
    {
        _languageContext = languageContext;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<string>> List() => Ok(_languageContext.SupportedLanguages);
}
