using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

public class QuizzesController : Controller
{
    public IActionResult Index() => View();

    public IActionResult Settings() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Start(string mode)
    {
        return (mode ?? "flashcards").ToLowerInvariant() switch
        {
            "typing" => RedirectToAction(nameof(Type)),
            _ => RedirectToAction(nameof(Flashcard))
        };
    }

    public IActionResult Flashcard() => View();

    public IActionResult Type() => View();
}
