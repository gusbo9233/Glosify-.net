using Glosify.Services.Speaking;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[Authorize]
public sealed class SpeakingController : Controller
{
    [HttpGet("/Speaking")]
    public IActionResult Index()
    {
        ViewData["HideAssistantPanel"] = true;
        return View(SpeakingAvatarCatalog.CreatePageViewModel());
    }
}
