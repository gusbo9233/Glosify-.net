using Glosify.Models.Entities;
using Glosify.Infrastructure.Api;
using Glosify.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[Authorize(AuthenticationSchemes = "Identity.Application")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class GameAccessController(SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> users, AdministratorAccess administrators, IConfiguration configuration) : Controller
{
    private async Task<ApplicationUser?> ValidUser()
    {
        var user = await signInManager.ValidateSecurityStampAsync(User);
        return user is not null && await signInManager.CanSignInAsync(user)
            && !(users.SupportsUserLockout && await users.IsLockedOutAsync(user)) ? user : null;
    }

    [HttpGet("/api/game/access")]
    public async Task<IActionResult> Access()
    {
        if (!configuration.GetValue<bool>("SharedAuth:Enabled")) return AccessError(404);
        var user = await ValidUser();
        if (user is null) return AccessError(401);
        if (!administrators.IsAdminUser(user.Id)) return AccessError(403);
        return Json(new { userId = user.Id });
    }

    private IActionResult AccessError(int status) => GlosifyProblemDetails.Result(
        HttpContext, status, GlosifyProblemDetails.CodeForStatus(status));

    [HttpGet("/sso/game")]
    public async Task<IActionResult> Game(Guid? quizId, [FromServices] Glosify.Data.GlosifyContext db)
    {
        if (!configuration.GetValue<bool>("SharedAuth:Enabled")) return NotFound();
        var user = await ValidUser();
        if (user is null)
        {
            await signInManager.SignOutAsync();
            var localReturn = "/sso/game" + (quizId is { } selectedQuiz ? "?quizId=" + selectedQuiz.ToString("D") : "");
            return Redirect("/login?returnUrl=" + Uri.EscapeDataString(localReturn));
        }
        if (!administrators.IsAdminUser(user.Id)) return StatusCode(StatusCodes.Status403Forbidden);
        if (quizId is { } id && !await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(db.Quizzes, q => q.Id == id && q.UserId == user.Id, HttpContext.RequestAborted))
            return AccessError(404);
        return Redirect("https://game.globeglotter.app/" + (quizId is { } selected ? "?quizId=" + selected.ToString("D") : ""));
    }
}
