using Glosify.Data;
using Glosify.Infrastructure.Api;
using Glosify.Models.Entities;
using Glosify.Services.Auth;
using Glosify.Services.Game;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Controllers;

[ApiController]
public sealed class GameUsageController(GlosifyContext db, IConfiguration configuration) : ControllerBase
{
    [AllowAnonymous, IgnoreAntiforgeryToken, HttpPost("/api/game/internal/usage"), RequestSizeLimit(16000)]
    public async Task<IActionResult> Ingest(GameUsageEvent input, CancellationToken cancellation)
    {
        Response.Headers.CacheControl = "no-store";
        if (!GameServiceAuthentication.IsTrusted(HttpContext)) return Error(403);
        if (input.Id == Guid.Empty || !Guid.TryParse(input.SessionId, out _) || string.IsNullOrEmpty(input.UserId) || input.UserId.Length > 450
            || new[] { input.Provider, input.Model, input.Endpoint, input.Operation, input.ServiceTier, input.Outcome, input.Measurement }.Any(s => s is null || s.Length > 100)
            || input.InputTokens is < 0 or > 100000000 || input.OutputTokens is < 0 or > 100000000 || input.CachedInputTokens is < 0
            || input.CacheWriteTokens is < 0 || (input.CachedInputTokens ?? 0) + (input.CacheWriteTokens ?? 0) > input.InputTokens || input.Characters is < 0 or > 100000000 || input.AudioSeconds is < 0 or > 10000000
            || input.Measurement is not ("actual" or "estimated" or "unknown")
            || input.StartedAt > DateTimeOffset.UtcNow.AddMinutes(5) || input.StartedAt < DateTimeOffset.UtcNow.AddYears(-1)) return Error(400);
        var existing = await db.Set<GameUsageEvent>().SingleOrDefaultAsync(e => e.Id == input.Id, cancellation);
        if (existing is not null && (existing.UserId != input.UserId || existing.SessionId != input.SessionId)) return Error(409);
        // A delayed pending message cannot overwrite the completed provider attempt.
        if (existing is not null && (existing.Outcome != "pending" || input.Outcome == "pending")) return NoContent();
        input.RecordedAt = DateTimeOffset.UtcNow;
        GameUsagePricing.Apply(input, (configuration.GetSection("GameIntegration:Rates").Get<GameUsageRate[]>() ?? []).Concat(GameUsagePricing.DefaultRates()));
        if (existing is null) db.Add(input); else db.Entry(existing).CurrentValues.SetValues(input);
        var session = await db.Set<GamePlaySession>().SingleOrDefaultAsync(s => s.Id == input.SessionId, cancellation);
        if (session is not null && session.UserId != input.UserId) return Error(409);
        if (session is null) { session = new() { Id = input.SessionId, UserId = input.UserId, StartedAt = input.StartedAt }; db.Add(session); }
        if (input.StartedAt < session.StartedAt) session.StartedAt = input.StartedAt;
        if (input.StartedAt > session.LastSeenAt) session.LastSeenAt = input.StartedAt;
        if (input.Operation == "session.active" && input.AudioSeconds is { } active) session.ActiveSeconds = Math.Max(session.ActiveSeconds, (long)active);
        try { await db.SaveChangesAsync(cancellation); }
        catch (DbUpdateException) { return Error(409); } // Retry resolves concurrent inserts by ID.
        return NoContent();
    }
    private IActionResult Error(int status) => GlosifyProblemDetails.Result(HttpContext, status, GlosifyProblemDetails.CodeForStatus(status));
}

[Authorize(AuthenticationSchemes = "Identity.Application", Policy = AuthorizationPolicyNames.AiCreditAdmin)]
public sealed class GameUsageAdminController(GlosifyContext db) : Controller
{
    [HttpGet("/Admin/GameUsage"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Index(DateTimeOffset? from, DateTimeOffset? to, string? admin, string? provider,
        string? model, string? operation, string? sessionId, CancellationToken cancellation)
    {
        var start = from ?? DateTimeOffset.UtcNow.AddDays(-30); var end = to ?? DateTimeOffset.UtcNow;
        if (end < start || end - start > TimeSpan.FromDays(366)) return BadRequest("Choose a date range of up to one year.");
        var query = db.Set<GameUsageEvent>().AsNoTracking().Where(e => e.StartedAt >= start && e.StartedAt <= end);
        if (!string.IsNullOrEmpty(admin)) query = query.Where(e => e.UserId == admin);
        if (!string.IsNullOrEmpty(provider)) query = query.Where(e => e.Provider == provider);
        if (!string.IsNullOrEmpty(model)) query = query.Where(e => e.Model == model);
        if (!string.IsNullOrEmpty(operation)) query = query.Where(e => e.Operation == operation);
        if (!string.IsNullOrEmpty(sessionId)) query = query.Where(e => e.SessionId == sessionId);
        var providerQuery = query.Where(e => e.Provider != "game");
        ViewData["EstimatedUsd"] = await providerQuery.SumAsync(e => e.EstimatedUsd ?? 0, cancellation);
        ViewData["Unpriced"] = await providerQuery.CountAsync(e => e.EstimatedUsd == null, cancellation);
        ViewData["Pending"] = await providerQuery.CountAsync(e => e.Outcome == "pending", cancellation);
        ViewData["Latest"] = await query.MaxAsync(e => (DateTimeOffset?)e.RecordedAt, cancellation);
        ViewData["Sessions"] = await query.Select(e => e.SessionId).Distinct().CountAsync(cancellation);
        // Subtract the last cumulative snapshot before the interval from each session's latest snapshot.
        var activity = db.Set<GameUsageEvent>().AsNoTracking().Where(e => e.StartedAt <= end && e.Operation == "session.active"
            && query.Select(q => q.SessionId).Contains(e.SessionId));
        var activityTotals = await activity.GroupBy(e => e.SessionId).Select(g => new
        {
            Latest = g.Max(e => e.AudioSeconds ?? 0),
            Before = g.Max(e => e.StartedAt < start ? e.AudioSeconds ?? 0 : 0)
        }).ToListAsync(cancellation);
        ViewData["ActiveSeconds"] = activityTotals.Sum(a => Math.Max(0, a.Latest - a.Before));
        ViewData["Breakdown"] = await providerQuery.GroupBy(e => new { e.Provider, e.Model, e.Operation })
            .Select(g => new GameUsageBreakdown(g.Key.Provider, g.Key.Model, g.Key.Operation,
                g.Count(), g.Sum(e => e.EstimatedUsd ?? 0), g.Count(e => e.EstimatedUsd == null)))
            .ToListAsync(cancellation);
        ViewData["From"] = start; ViewData["To"] = end;
        return View("~/Views/Admin/GameUsage.cshtml", await query.OrderByDescending(e => e.StartedAt).Take(500).ToListAsync(cancellation));
    }
}

public sealed record GameUsageBreakdown(string Provider, string Model, string Operation, int Calls, decimal EstimatedUsd, int Unpriced);
