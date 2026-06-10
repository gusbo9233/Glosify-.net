using Glosify.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

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

    public IActionResult Index() => View();

    [AllowAnonymous]
    public IActionResult Privacy() => View();

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
