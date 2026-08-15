using System.Diagnostics;
using Glosify.Models.ViewModels;
using Glosify.Services;
using Glosify.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly IWebHostEnvironment _environment;

    public HomeController(ILogger<HomeController> logger, IWebHostEnvironment environment)
    {
        _logger = logger;
        _environment = environment;
    }

    [AllowAnonymous]
    public IActionResult Index()
    {
        ViewData["LocalizedPublicPage"] = "home";
        return View();
    }

    [AllowAnonymous]
    public IActionResult LocalizedIndex(string culture) => LocalizedPublicView(culture, "Index", "home");

    [AllowAnonymous]
    public IActionResult Privacy()
    {
        ViewData["LocalizedPublicPage"] = "privacy";
        return View();
    }

    [AllowAnonymous]
    public IActionResult LocalizedPrivacy(string culture) => LocalizedPublicView(culture, "Privacy", "privacy");

    [AllowAnonymous]
    [HttpGet("/privacy/english")]
    public IActionResult PrivacyEnglish()
    {
        ViewData["LocalizedPublicPage"] = "privacy";
        return View("~/Views/Home/Privacy.cshtml");
    }

    [AllowAnonymous]
    public IActionResult Terms()
    {
        ViewData["LocalizedPublicPage"] = "terms";
        return View();
    }

    [AllowAnonymous]
    public IActionResult LocalizedTerms(string culture) => LocalizedPublicView(culture, "Terms", "terms");

    [AllowAnonymous]
    [HttpGet("/terms/english")]
    public IActionResult TermsEnglish()
    {
        ViewData["LocalizedPublicPage"] = "terms";
        return View("~/Views/Home/Terms.cshtml");
    }

    [AllowAnonymous]
    public IActionResult Support()
    {
        ViewData["LocalizedPublicPage"] = "support";
        return View();
    }

    [AllowAnonymous]
    public IActionResult LocalizedSupport(string culture) => LocalizedPublicView(culture, "Support", "support");

    [AllowAnonymous]
    [HttpGet("/support/english")]
    public IActionResult SupportEnglish()
    {
        ViewData["LocalizedPublicPage"] = "support";
        return View("~/Views/Home/Support.cshtml");
    }

    private IActionResult LocalizedPublicView(string culture, string viewName, string page)
    {
        if (!DisplayCultureCatalog.IsLocalizedPublicCulture(culture)
            || !DisplayCultureCatalog.TryCanonicalize(culture, out var canonicalCulture))
        {
            return NotFound();
        }

        ViewData["LocalizedPublicPage"] = page;
        if (User.Identity?.IsAuthenticated != true)
        {
            DisplayCultureCookie.Append(Response, Request, canonicalCulture);
        }
        DisplayLanguageTelemetry.Record(canonicalCulture, "localized-public-url");
        return View(viewName);
    }

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        var exceptionFeature = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
        if (exceptionFeature?.Error != null)
        {
            _logger.LogError(
                exceptionFeature.Error,
                "Unhandled exception while processing {Path}. TraceIdentifier: {TraceIdentifier}",
                exceptionFeature.Path,
                HttpContext.TraceIdentifier);
        }

        if (exceptionFeature?.Error != null && ServiceWarmupMessage.IsDatabaseWarmupFailure(exceptionFeature.Error))
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return View(new ErrorViewModel
            {
                RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier,
                Title = "Services are warming up",
                Message = ServiceWarmupMessage.Dependencies,
                ReturnPath = exceptionFeature.Path,
                IsServiceWarmup = true
            });
        }

        return View(new ErrorViewModel
        {
            RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier,
            Message = _environment.IsDevelopment() && exceptionFeature?.Error != null
                ? exceptionFeature.Error.ToString()
                : "An error occurred while processing your request."
        });
    }
}
