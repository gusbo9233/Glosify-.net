using System.Security.Claims;
using System.Text.Json;
using Glosify.Data;
using Glosify.Infrastructure.Api;
using Glosify.Models.Entities;
using Glosify.Services.Game;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Controllers;

[ApiController, Authorize(AuthenticationSchemes = "Identity.Application"), GameAdmin]
[Route("api/game")]
public sealed class GameDataController(GlosifyContext db) : ControllerBase
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private IActionResult Error(int status) => GlosifyProblemDetails.Result(HttpContext, status, GlosifyProblemDetails.CodeForStatus(status));

    [HttpGet("quizzes")]
    public async Task<IActionResult> Quizzes(CancellationToken cancellation) => Ok(new
    {
        quizzes = await db.Quizzes.AsNoTracking().Where(q => q.UserId == UserId).OrderBy(q => q.Name)
            .Select(q => new { q.Id, q.Name, q.SourceLanguage, q.TargetLanguage }).ToListAsync(cancellation)
    });

    [HttpGet("quizzes/{id:guid}")]
    public async Task<IActionResult> Quiz(Guid id, CancellationToken cancellation)
    {
        var quiz = await db.Quizzes.AsNoTracking().SingleOrDefaultAsync(q => q.Id == id && q.UserId == UserId, cancellation);
        if (quiz is null) return Error(404);
        var words = await db.Words.AsNoTracking().Where(w => w.QuizId == id).OrderBy(w => w.Id)
            .Select(w => new StudyItem(w.Lemma, w.Translation)).Take(40).ToListAsync(cancellation);
        var sentences = await db.QuizSentences.AsNoTracking().Where(w => w.QuizId == id).OrderBy(w => w.Id)
            .Select(w => new StudyItem(w.Text, w.Translation)).Take(40).ToListAsync(cancellation);
        return Ok(new { quiz.Id, quiz.Name, quiz.SourceLanguage, quiz.TargetLanguage, words = Bound(words), sentences = Bound(sentences) });
    }
    private static List<StudyItem> Bound(IEnumerable<StudyItem> source)
    {
        var result = new List<StudyItem>(); var remaining = 6000;
        foreach (var item in source)
        {
            var text = item.Text[..Math.Min(500, item.Text.Length)];
            var translation = item.Translation[..Math.Min(500, item.Translation.Length)];
            if (text.Length + translation.Length > remaining) break;
            remaining -= text.Length + translation.Length;
            if (!string.IsNullOrWhiteSpace(text)) result.Add(new(text, translation));
        }
        return result;
    }

    [HttpGet("profile")]
    public async Task<IActionResult> Profile(CancellationToken cancellation)
    {
        var profile = await db.Set<GamePlayerProfile>().AsNoTracking().SingleOrDefaultAsync(p => p.UserId == UserId, cancellation);
        return Ok(new { version = profile?.Version, character = profile is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(profile.CharacterJson) });
    }

    // Browser writes go through the game host's antiforgery-protected proxy. This
    // endpoint additionally requires the server credential, never sent to a browser.
    [HttpPut("profile"), IgnoreAntiforgeryToken, RequestSizeLimit(24000)]
    public async Task<IActionResult> SaveProfile(ProfileWrite input, CancellationToken cancellation)
    {
        if (!GameServiceAuthentication.IsTrusted(HttpContext)) return Error(403);
        if (!GameProfileValidation.IsValid(input.Character)) return Error(400);
        if (input.Character.TryGetProperty("quizId", out var selected) && selected.ValueKind == JsonValueKind.String
            && (!Guid.TryParse(selected.GetString(), out var quizId) || !await db.Quizzes.AnyAsync(q => q.Id == quizId && q.UserId == UserId, cancellation))) return Error(404);
        var profile = await db.Set<GamePlayerProfile>().SingleOrDefaultAsync(p => p.UserId == UserId, cancellation);
        if (profile?.Version != input.Version) return Error(409);
        if (profile is null) { profile = new() { UserId = UserId }; db.Add(profile); }
        profile.CharacterJson = input.Character.GetRawText();
        profile.Version = Guid.NewGuid(); profile.UpdatedAt = DateTimeOffset.UtcNow;
        try { await db.SaveChangesAsync(cancellation); }
        catch (DbUpdateException) { return Error(409); }
        return Ok(new { profile.Version });
    }
    public sealed record StudyItem(string Text, string Translation);
    public sealed record ProfileWrite(Guid? Version, JsonElement Character);
}
