using Glosify.Data;
using Glosify.Models;
using Glosify.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
namespace Glosify.Controllers
{
    public class HomeController : Controller
    {
        private readonly GlosifyContext _context;
        private readonly ILogger<HomeController> _logger;
        private readonly IConfiguration _configuration;

        public HomeController(GlosifyContext context, ILogger<HomeController> logger, IConfiguration configuration)
        {
            _context = context;
            _logger = logger;
            _configuration = configuration;
        }

        public IActionResult Index()
        {
            return View();
        }

        public IActionResult Privacy()
        {
            return View();
        }

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
}
