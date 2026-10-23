using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Avatar;

public sealed class AvatarTranscriptService(GlosifyContext db, AvatarSessions sessions, TimeProvider clock)
{
    public async Task InitializePreferenceAsync(string userId, CancellationToken ct)
    {
        using var lease = await sessions.LockTranscriptWritesAsync(userId, ct);
        await ReadPreferenceAsync(userId, ct);
    }

    public async Task SetPreferenceAsync(string userId, bool enabled, CancellationToken ct)
    {
        using var lease = await sessions.LockTranscriptWritesAsync(userId, ct);
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        user.AutoSaveAvatarConversations = enabled;
        await db.SaveChangesAsync(ct);
        // A successful disable waits for prior saves and prevents subsequent queued
        // automatic saves from writing; a failed preference update changes no cache.
        sessions.SetAutoSavePreference(userId, enabled);
    }

    private async Task<bool> ReadPreferenceAsync(string userId, CancellationToken ct)
    {
        var enabled = await db.Users.Where(u => u.Id == userId).Select(u => u.AutoSaveAvatarConversations).SingleAsync(ct);
        sessions.SetAutoSavePreference(userId, enabled);
        return enabled;
    }

    public async Task<AvatarTranscriptStatus> StatusAsync(Guid id, string userId, string language, CancellationToken ct)
    {
        sessions.CachedTranscriptStatus(id, userId, language); // Validate ownership before accessing storage.
        using var lease = await sessions.LockTranscriptWritesAsync(userId, ct);
        using var retained = sessions.RetainTranscript(id, userId, language);
        var transcript = await db.RealtimeTranslationTranscripts.AsNoTracking()
            .Where(t => t.AvatarConversationId == id && t.UserId == userId && t.TargetLanguage == language)
            .Select(t => new { t.Id, Count = t.Segments.Count }).SingleOrDefaultAsync(ct);
        await ReadPreferenceAsync(userId, ct);
        var saved = sessions.SavedTranscript(id, userId, language);
        sessions.SetSavedTranscript(id, userId, transcript?.Id,
            saved.Suppressed || transcript is null && saved.Id is not null, transcript?.Count ?? 0);
        return sessions.CachedTranscriptStatus(id, userId, language);
    }

    public async Task<AvatarTranscriptStatus> SaveAsync(Guid id, string userId, string language, bool automatic, CancellationToken ct)
    {
        var cached = sessions.CachedTranscriptStatus(id, userId, language);
        // The voice path needs no database or save lock when saving is off. Session
        // creation primes the preference; direct callers initialize it once below.
        if (automatic && (cached.Suppressed || sessions.AutoSavePreference(id, userId, language) == false))
            return cached;
        using var lease = await sessions.LockTranscriptWritesAsync(userId, ct);
        using var retained = sessions.RetainTranscript(id, userId, language);
        var auto = await ReadPreferenceAsync(userId, ct);
        cached = sessions.CachedTranscriptStatus(id, userId, language);
        if (automatic && (!auto || cached.Suppressed) || cached.TurnCount == 0) return cached;
        var snapshot = sessions.Snapshot(id, userId, language);
        var transcript = await db.RealtimeTranslationTranscripts
            .SingleOrDefaultAsync(t => t.AvatarConversationId == id && t.UserId == userId && t.TargetLanguage == language, ct);
        if (automatic && transcript is null && cached.TranscriptId is not null)
        {
            sessions.SetSavedTranscript(id, userId, null, true);
            return sessions.CachedTranscriptStatus(id, userId, language);
        }
        if (transcript is null)
        {
            transcript = new() { UserId = userId, TargetLanguage = language, SourceKind = RealtimeTranslationTranscriptSourceKinds.Avatar,
                AvatarConversationId = id, Stream = RealtimeTranslationTranscriptStreams.Source,
                Title = $"Conversation with {snapshot.Turns.FirstOrDefault(t => t.Role == "assistant")?.Speaker ?? AvatarCharacter.Find(snapshot.Character)?.Name ?? "avatar"} · {snapshot.Turns[0].CapturedAt:yyyy-MM-dd}",
                CreatedAt = snapshot.Turns[0].CapturedAt };
            db.RealtimeTranslationTranscripts.Add(transcript);
        }
        var count = await db.RealtimeTranslationTranscriptSegments.CountAsync(s => s.TranscriptId == transcript.Id, ct);
        foreach (var turn in snapshot.Turns.Skip(count))
            db.RealtimeTranslationTranscriptSegments.Add(new() { TranscriptId = transcript.Id, SessionId = id,
                Sequence = turn.Sequence, ProviderEventKey = $"{turn.TurnId}:{turn.Role}", Stream = RealtimeTranslationTranscriptStreams.Source,
                Text = turn.Text, Speaker = turn.Speaker, CapturedAt = turn.CapturedAt });
        if (snapshot.Turns.Length > count)
        {
            transcript.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct); // Existing resource accounting enforces storage quotas.
        }
        sessions.SetSavedTranscript(id, userId, transcript.Id, false, snapshot.Turns.Length);
        return sessions.CachedTranscriptStatus(id, userId, language);
    }
}
