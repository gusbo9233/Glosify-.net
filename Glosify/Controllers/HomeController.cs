using Glosify.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace Glosify.Controllers;

public class HomeController : Controller
{
    public IActionResult Index() => View();

    [AllowAnonymous]
    public IActionResult Privacy() => View();

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        var exceptionFeature = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
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

        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
