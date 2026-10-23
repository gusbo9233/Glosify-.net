using Glosify.Services.Guest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[AllowAnonymous]
[Route("preview")]
public sealed class PreviewController : Controller
{
    [HttpGet("{**path}")]
    public IActionResult Unknown() => NotFound();

    [HttpGet("{section}/spanish")] // Compatibility with previously shared sample links.
    [HttpGet("{section}/sample")]
    public IActionResult Details(string section, string? stream = null)
    {
        if (GuestSamples.Find(section) is null) return NotFound();
        ViewData["GuestSample"] = section;
        var view = section switch { "books" => "Books/Read", "quiz" => "Quiz/Details", "anki" => "Anki/Index", "transcripts" => "Transcripts/Details", _ => "Translations/Details" };
        return View($"~/Views/{view}.cshtml", GuestSamples.For(HttpContext).Detail(section, stream));
    }

    [HttpGet("quiz/spanish/settings")]
    [HttpGet("quiz/sample/settings")]
    public IActionResult Settings()
    {
        ViewData["GuestSample"] = "quiz";
        return View("~/Views/Quiz/Settings.cshtml", GuestSamples.For(HttpContext).Settings());
    }

    [HttpGet("{section}/spanish/practice/{mode}")]
    [HttpGet("{section}/sample/practice/{mode}")]
    public IActionResult Practice(string section, string mode, string? practiceDirection = null)
    {
        if (section is not ("quiz" or "anki") || mode is not ("typing" or "flashcards")) return NotFound();
        ViewData["GuestSample"] = section;
        ViewData["GuestPractice"] = mode;
        return mode == "typing"
            ? View("~/Views/TypingQuiz/Index.cshtml", GuestSamples.For(HttpContext).Typing(practiceDirection))
            : View("~/Views/FlashcardQuiz/Index.cshtml", GuestSamples.For(HttpContext).Flashcards(practiceDirection));
    }
}
