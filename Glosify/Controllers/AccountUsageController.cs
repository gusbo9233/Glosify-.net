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
        while (true)
        {
            var attempts = await db.QuizAttempts.Where(x => x.UserId == user).Take(100).ToListAsync(ct);
            var reviews = await db.AnkiReviews.Where(x => x.Collection.UserId == user).Take(100).ToListAsync(ct);
            if (attempts.Count == 0 && reviews.Count == 0) break;
            db.QuizAttempts.RemoveRange(attempts);
            db.AnkiReviews.RemoveRange(reviews);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
        return RedirectToAction(nameof(Index));
    }
}
