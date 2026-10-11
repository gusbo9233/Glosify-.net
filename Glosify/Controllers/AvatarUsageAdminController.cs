using System.Globalization;
using Glosify.Infrastructure.Api;
using Glosify.Services.Auth;
using Glosify.Services.Avatar;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[Authorize(AuthenticationSchemes = "Identity.Application", Policy = AuthorizationPolicyNames.AiCreditAdmin)]
public sealed class AvatarUsageAdminController(AvatarUsageReports reports, TimeProvider clock) : Controller
{
    [HttpGet("/Admin/AvatarUsage"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Index(string? from, string? to, string? admin,
        string? provider, string? model, string? operation, string? sessionId, CancellationToken cancellation)
    {
        var end = clock.GetUtcNow();
        if (!string.IsNullOrWhiteSpace(to) && !TryDate(to, out end)) return InvalidFilters();
        var start = end > DateTimeOffset.MinValue.AddDays(30) ? end.AddDays(-30) : DateTimeOffset.MinValue;
        if (!string.IsNullOrWhiteSpace(from) && !TryDate(from, out start)) return InvalidFilters();
        if (!ModelState.IsValid || end < start || end - start > TimeSpan.FromDays(366)
            || admin?.Length > 450 || new[] { provider, model, operation, sessionId }.Any(x => x?.Length > 128))
            return InvalidFilters();
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            if (!Guid.TryParse(sessionId, out var id))
                return GlosifyProblemDetails.Result(HttpContext, 400, GlosifyProblemDetails.CodeForStatus(400), "Choose a valid session ID.");
            sessionId = id.ToString();
        }
        var report = await reports.ReadAsync(new(start, end, admin?.Trim(), provider?.Trim(), model?.Trim(), operation?.Trim(), sessionId), cancellation);
        return View("~/Views/Admin/AvatarUsage.cshtml", report);
    }
    private IActionResult InvalidFilters() => GlosifyProblemDetails.Result(HttpContext, 400,
        GlosifyProblemDetails.CodeForStatus(400), "Choose valid filters and a date range of up to one year.");
    private static bool TryDate(string value, out DateTimeOffset date) => DateTimeOffset.TryParse(value,
        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date);
}
