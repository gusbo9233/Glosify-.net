using Glosify.Data;
using Glosify.Extensions;
using Glosify.Services.Abuse;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Controllers;

[Authorize]
[Route("account/usage")]
public sealed class AccountUsageController(ResourceQuotaService quotas, GlosifyContext db) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct) => View(await quotas.GetUsageAsync(User.GetUserId(), ct));

    [HttpPost("clear-practice-history")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearPracticeHistory(CancellationToken ct)
    {
        var user = User.GetUserId();
        db.QuizAttempts.RemoveRange(await db.QuizAttempts.Where(x => x.UserId == user).ToListAsync(ct));
        db.AnkiReviews.RemoveRange(await db.AnkiReviews.Where(x => x.Collection.UserId == user).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Index));
    }
}
