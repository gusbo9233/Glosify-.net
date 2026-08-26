using System.Text.Json;
using Glosify.Data;
using Glosify.Extensions;
using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Models.ViewModels;
using Glosify.Services;
using Glosify.Services.Ai;
using Glosify.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Controllers;

[Authorize(Policy = AuthorizationPolicyNames.AiCreditAdmin)]
public sealed class AdminController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAiCreditService _credits;
    private readonly ICreditPricingResolver _pricing;
    private readonly GlosifyContext _context;

    public AdminController(
        UserManager<ApplicationUser> userManager,
        IAiCreditService credits,
        ICreditPricingResolver pricing,
        GlosifyContext context)
    {
        _userManager = userManager;
        _credits = credits;
        _pricing = pricing;
        _context = context;
    }

    [HttpGet("/Admin/TranslationCaptures")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> TranslationCaptures(
        Guid? sessionId,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var sessions = _context.RealtimeTranslationSessions
            .AsNoTracking()
            .Where(session => session.UserId == userId && session.CaptureEvents.Any());

        var session = sessionId.HasValue
            ? await sessions.SingleOrDefaultAsync(
                candidate => candidate.Id == sessionId.Value,
                cancellationToken)
            : await sessions
                .OrderByDescending(candidate => candidate.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
        if (session is null)
        {
            return NotFound();
        }

        var events = await _context.RealtimeTranslationCaptureEvents
            .AsNoTracking()
            .Where(captured => captured.SessionId == session.Id)
            .OrderBy(captured => captured.Ordinal)
            .Select(captured => new
            {
                captured.Ordinal,
                captured.Sequence,
                captured.Stage,
                captured.Kind,
                captured.Text,
                captured.SourceText,
                captured.SourceLanguage,
                captured.TargetLanguage,
                captured.ProviderRequest,
                captured.CapturedAt,
                captured.StoredAt,
            })
            .ToListAsync(cancellationToken);

        var payload = new
        {
            session = new
            {
                session.Id,
                session.TranslationMode,
                session.SpeechProvider,
                session.SourceLanguage,
                session.TargetLanguage,
                session.Model,
                session.Status,
                session.CreatedAt,
                session.StartedAt,
                session.EndedAt,
            },
            events,
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        return File(json, "application/json", $"translation-capture-{session.Id:N}.json");
    }

    [HttpGet]
    public async Task<IActionResult> AiCredits(string? search, string? selectedUserId, CancellationToken cancellationToken)
    {
        var users = await SearchUsersAsync(search, selectedUserId, cancellationToken);
        var rows = new List<AiCreditUserRow>();
        foreach (var user in users)
        {
            var account = await _credits.GetOrCreateAccountAsync(user.Id, cancellationToken);
            rows.Add(new AiCreditUserRow
            {
                UserId = user.Id,
                Email = user.Email ?? user.UserName ?? user.Id,
                BalanceCredits = account.BalanceCredits,
                ReservedCredits = account.ReservedCredits,
                AvailableCredits = account.AvailableCredits,
                TrialGrantedAt = account.TrialGrantedAt,
            });
        }

        var selected = !string.IsNullOrWhiteSpace(selectedUserId)
            ? rows.FirstOrDefault(row => row.UserId == selectedUserId)
            : rows.FirstOrDefault();
        var transactions = selected == null
            ? []
            : await _credits.GetRecentTransactionsAsync(selected.UserId, 30, cancellationToken);

        return View(new AiCreditAdminViewModel
        {
            Search = search,
            Users = rows,
            SelectedUser = selected,
            RecentTransactions = transactions.Select(AiCreditTransactionRow.From).ToList(),
            Pricing = _pricing.GetCatalog(),
            Grant = new AiCreditGrantInput
            {
                UserId = selected?.UserId ?? string.Empty,
                Search = search,
            }
        });
    }

    [HttpPost]
    public async Task<IActionResult> GrantAiCredits(AiCreditGrantInput input, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input.UserId))
        {
            TempData[NotificationKeys.Admin] = "Choose a user first.";
            return RedirectToAction(nameof(AiCredits), new { search = input.Search });
        }

        if (input.Credits <= 0)
        {
            TempData[NotificationKeys.Admin] = "Credits must be greater than zero.";
            return RedirectToAction(nameof(AiCredits), new { search = input.Search, selectedUserId = input.UserId });
        }

        if (string.IsNullOrWhiteSpace(input.Note))
        {
            TempData[NotificationKeys.Admin] = "Add a note for the credit grant.";
            return RedirectToAction(nameof(AiCredits), new { search = input.Search, selectedUserId = input.UserId });
        }

        var targetExists = await _userManager.Users.AnyAsync(user => user.Id == input.UserId, cancellationToken);
        if (!targetExists)
        {
            TempData[NotificationKeys.Admin] = "User not found.";
            return RedirectToAction(nameof(AiCredits), new { search = input.Search });
        }

        await _credits.GrantAsync(
            User.GetUserId(),
            input.UserId,
            input.Credits,
            input.Note,
            cancellationToken);
        TempData[NotificationKeys.Admin] = $"Granted {input.Credits} credits.";
        return RedirectToAction(nameof(AiCredits), new { search = input.Search, selectedUserId = input.UserId });
    }

    private async Task<IReadOnlyList<ApplicationUser>> SearchUsersAsync(
        string? search,
        string? selectedUserId,
        CancellationToken cancellationToken)
    {
        var query = _userManager.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(user =>
                (user.Email != null && user.Email.Contains(term))
                || (user.UserName != null && user.UserName.Contains(term)));
        }

        var users = await query
            .OrderBy(user => user.Email ?? user.UserName)
            .Take(25)
            .ToListAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(selectedUserId) && users.All(user => user.Id != selectedUserId))
        {
            var selected = await _userManager.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(user => user.Id == selectedUserId, cancellationToken);
            if (selected != null)
            {
                users.Insert(0, selected);
            }
        }

        return users;
    }
}
