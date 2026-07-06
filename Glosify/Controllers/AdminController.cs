using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Glosify.Services;
using Glosify.Services.Ai;

namespace Glosify.Controllers;

[Authorize(Policy = "AiCreditAdmin")]
public sealed class AdminController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAiCreditService _credits;

    public AdminController(
        UserManager<ApplicationUser> userManager,
        IAiCreditService credits)
    {
        _userManager = userManager;
        _credits = credits;
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
            RecentTransactions = transactions,
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
            TempData["AdminMessage"] = "Choose a user first.";
            return RedirectToAction(nameof(AiCredits), new { search = input.Search });
        }

        if (input.Credits <= 0)
        {
            TempData["AdminMessage"] = "Credits must be greater than zero.";
            return RedirectToAction(nameof(AiCredits), new { search = input.Search, selectedUserId = input.UserId });
        }

        if (string.IsNullOrWhiteSpace(input.Note))
        {
            TempData["AdminMessage"] = "Add a note for the credit grant.";
            return RedirectToAction(nameof(AiCredits), new { search = input.Search, selectedUserId = input.UserId });
        }

        var targetExists = await _userManager.Users.AnyAsync(user => user.Id == input.UserId, cancellationToken);
        if (!targetExists)
        {
            TempData["AdminMessage"] = "User not found.";
            return RedirectToAction(nameof(AiCredits), new { search = input.Search });
        }

        await _credits.GrantAsync(
            User.GetUserId(),
            input.UserId,
            input.Credits,
            input.Note,
            cancellationToken);
        TempData["AdminMessage"] = $"Granted {input.Credits} credits.";
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
