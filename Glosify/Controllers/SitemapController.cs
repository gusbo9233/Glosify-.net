using System.Xml.Linq;
using Glosify.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[AllowAnonymous]
public sealed class SitemapController(IWebHostEnvironment hostEnvironment) : Controller
{
    [HttpGet("/sitemap.xml")]
    [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any)]
    public IActionResult Index()
    {
        var origin = hostEnvironment.IsProduction()
            ? "https://glosify.se"
            : $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        var paths = new List<string> { "/", "/privacy/english", "/terms/english", "/support/english" };
        foreach (var culture in DisplayCultureCatalog.LocalizedPublicCultures)
        {
            paths.Add($"/{culture.Name}");
            paths.Add($"/{culture.Name}/privacy");
            paths.Add($"/{culture.Name}/terms");
            paths.Add($"/{culture.Name}/support");
        }

        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var document = new XDocument(new XElement(ns + "urlset",
            paths.Select(path => new XElement(ns + "url", new XElement(ns + "loc", origin + path)))));
        return Content(document.ToString(SaveOptions.DisableFormatting), "application/xml");
    }
}
