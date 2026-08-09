using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;

namespace Glosify.Services.Ai.Assistant.Tools;

/// <summary>
/// Loads the custom quiz a tool call is about, and works out whether that is a saved
/// document or one still queued as a pending change.
/// </summary>
internal sealed class CustomQuizToolStore
{
    private readonly GlosifyContext _context;

    public CustomQuizToolStore(GlosifyContext context) => _context = context;

    public async Task<CustomQuiz?> LoadOwnedCustomQuizAsync(Guid id, string userId, CancellationToken ct) =>
        await _context.CustomQuizzes
            .AsNoTracking()
            .Include(item => item.Quiz)
            .FirstOrDefaultAsync(item => item.Id == id && item.Quiz.UserId == userId, ct);

    public async Task<CustomQuizTarget> ResolveCustomQuizTargetAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken ct)
    {
        var supplied = GetString(args, "custom_quiz_id");
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            if (!Guid.TryParse(supplied, out var parsed))
            {
                return new(null, null, string.Empty, null, "custom_quiz_id must be a valid id.");
            }
            var item = await LoadOwnedCustomQuizAsync(parsed, context.UserId, ct);
            return item == null
                ? new(null, null, string.Empty, null, "That custom quiz was not found.")
                : new(item.Id, null, item.Name, item.DefinitionJson, null);
        }

        if (!string.IsNullOrWhiteSpace(context.PendingCustomQuizRef))
        {
            return new(null, context.PendingCustomQuizRef, context.PendingCustomQuizName ?? "New custom quiz", null, null);
        }

        if (context.CustomQuizId.HasValue)
        {
            var item = await LoadOwnedCustomQuizAsync(context.CustomQuizId.Value, context.UserId, ct);
            return item == null
                ? new(null, null, string.Empty, null, "The open custom quiz was not found.")
                : new(item.Id, null, item.Name, item.DefinitionJson, null);
        }

        return new(null, null, string.Empty, null, "Start or open a custom quiz before adding elements.");
    }

}

/// <summary>
/// Which custom quiz a tool call is about: a saved document, a queued draft, or neither.
/// </summary>
internal sealed record CustomQuizTarget(Guid? Id, string? DraftRef, string Name, string? DefinitionJson, string? Error);
