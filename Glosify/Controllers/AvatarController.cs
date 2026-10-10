using System.Security.Claims;
using System.Text.Json;
using Glosify.Data;
using Glosify.Infrastructure.Api;
using Glosify.Models.Entities;
using Glosify.Services.Ai;
using Glosify.Services.Auth;
using Glosify.Services.Avatar;
using Glosify.Services.Language;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glosify.Controllers;

[Authorize(AuthenticationSchemes = "Identity.Application", Policy = AuthorizationPolicyNames.AiCreditAdmin)]
[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AvatarController(IOptions<AvatarOptions> options, AvatarPricing pricing, AvatarSessions sessions,
    AvatarConversation conversation, AvatarBilling billing, GlosifyContext db, IAiCreditService credits, ILanguageContext languageContext,
    SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users) : Controller
{
    private QuizLanguage? CurrentLanguage => QuizLanguageCatalog.Find(languageContext.CurrentLanguage) is { IsLanguageLearning: true } language ? language : null;
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpGet("/Avatar")]
    public IActionResult Index()
    {
        if (!options.Value.Enabled) return NotFound();
        Response.Headers["Permissions-Policy"] = "microphone=(self), camera=(), geolocation=(), payment=(), usb=(), interest-cohort=()";
        return View();
    }

    [HttpGet("/api/avatar/config")]
    public async Task<IActionResult> Config(CancellationToken ct)
    {
        if (await Access() is { } failure) return failure;
        var language = CurrentLanguage;
        var quizzes = await db.Quizzes.AsNoTracking().Where(x => x.UserId == UserId)
            .WhereTargetLanguage(language?.Code).OrderBy(x => x.Name)
            .Select(x => new { x.Id, x.Name, x.TargetLanguage }).ToListAsync(ct);
        var account = await credits.GetOrCreateAccountAsync(UserId, ct);
        return Json(new { available = pricing.Available, rates = pricing.Rates, balance = account.AvailableCredits,
            language = language?.Code, languageName = language?.Name,
            languages = QuizLanguageCatalog.LanguageLearning.Where(x => x.Code == language?.Code).Select(x => new { x.Code, x.Name }),
            quizzes });
    }

    [HttpPost("/api/avatar/sessions"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Start([FromBody] AvatarStartRequest request, CancellationToken ct)
    {
        if (await Access() is { } failure) return failure;
        if (!pricing.Available) return ProblemResult(503, "Voice is not configured. Ask an administrator to configure the provider keys.");
        var language = CurrentLanguage;
        if (language is null) return ProblemResult(422, "Choose a learning language in the sidebar to start a conversation.");
        var practice = "Free conversation. Follow the user's interests.";
        if (request.QuizId is { } id)
        {
            var quiz = await db.Quizzes.AsNoTracking().WhereTargetLanguage(language.Code)
                .SingleOrDefaultAsync(x => x.Id == id && x.UserId == UserId, ct);
            if (quiz is null) return ProblemResult(404, "Quiz not found.");
            var words = await db.Words.AsNoTracking().Where(x => x.QuizId == id).OrderBy(x => x.CreatedAt).Take(40)
                .Select(x => new { Text = x.Lemma, x.Translation }).ToListAsync(ct);
            var sentences = await db.QuizSentences.AsNoTracking().Where(x => x.QuizId == id).OrderBy(x => x.CreatedAt).Take(20)
                .Select(x => new { x.Text, x.Translation }).ToListAsync(ct);
            if (words.Count + sentences.Count == 0) return ProblemResult(422, "This quiz has no words or sentences to practice.");
            static string Limit(string value) => value.Length > 160 ? value[..160] : value;
            practice = JsonSerializer.Serialize(new { name = Limit(quiz.Name), vocabulary = words.Select(x => new { text = Limit(x.Text), translation = Limit(x.Translation) }),
                sentences = sentences.Select(x => new { text = Limit(x.Text), translation = Limit(x.Translation) }) });
        }
        var account = await credits.GetOrCreateAccountAsync(UserId, ct);
        // Recognition reserves the maximum 45-second utterance. Replies and speech
        // reserve separately at the point of use, with rates displayed before Start.
        var required = CreditAmounts.RoundCharge(pricing.CreditRate("recognition") * 45);
        if (account.AvailableCredits < required) throw new InsufficientAiCreditsException(account.AvailableCredits, required);
        try
        {
            var session = sessions.Create(UserId, language, practice);
            return Json(new { sessionId = session.Id, language = language.Code, connectUrl = $"/api/avatar/sessions/{session.Id}/voice" });
        }
        catch (AvatarException ex) { return ProblemResult(ex.Status, ex.Message); }
    }

    [HttpGet("/api/avatar/sessions/{id:guid}/usage")]
    public async Task<IActionResult> Usage(Guid id, CancellationToken ct)
    {
        if (await Access() is { } failure) return failure;
        // Totals are always scoped to the authenticated owner, including after disconnect.
        return Json(await billing.TotalsAsync(UserId, id, ct));
    }

    [HttpPost("/api/avatar/sessions/{id:guid}/end"), ValidateAntiForgeryToken]
    public async Task<IActionResult> End(Guid id)
    {
        if (await Access() is { } failure) return failure;
        try { sessions.Remove(sessions.Get(id, UserId)); return NoContent(); }
        catch (AvatarException ex) when (ex.Status is 404 or 410) { return NoContent(); }
    }

    // HTTP/2 WebSockets use CONNECT rather than GET. The anti-forgery-protected
    // session creation, owner check and exact Origin check protect this upgrade.
    [Route("/api/avatar/sessions/{id:guid}/voice"), IgnoreAntiforgeryToken]
    public async Task<IActionResult> Voice(Guid id, CancellationToken ct)
    {
        if (await Access() is { } failure) return failure;
        var origin = Request.Headers.Origin.ToString();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.GetLeftPart(UriPartial.Authority) != $"{Request.Scheme}://{Request.Host}")
            return ProblemResult(403, "Voice connections must originate from this app.");
        if (!HttpContext.WebSockets.IsWebSocketRequest) return ProblemResult(400, "A voice WebSocket connection is required.");
        AvatarSession session;
        try { session = sessions.Get(id, UserId); }
        catch (AvatarException ex) { return ProblemResult(ex.Status, ex.Message); }
        if (Interlocked.CompareExchange(ref session.Connected, 1, 0) != 0) return ProblemResult(409, "This conversation is already connected.");
        try
        {
            using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
            await conversation.RunAsync(socket, session, User, ct);
        }
        finally { sessions.Remove(session); }
        return new EmptyResult();
    }

    private async Task<IActionResult?> Access()
    {
        Response.Headers.CacheControl = "no-store";
        if (!options.Value.Enabled) return ProblemResult(404, "Avatar preview is disabled.");
        var user = await signIn.ValidateSecurityStampAsync(User);
        if (user is null || !await signIn.CanSignInAsync(user) || await users.IsLockedOutAsync(user))
            return ProblemResult(401, "Please sign in again.");
        return null;
    }
    private ObjectResult ProblemResult(int status, string detail) => GlosifyProblemDetails.Result(HttpContext, status, GlosifyProblemDetails.CodeForStatus(status), detail);
}

public sealed record AvatarStartRequest(Guid? QuizId);
