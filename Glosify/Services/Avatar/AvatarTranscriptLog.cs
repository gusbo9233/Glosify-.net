namespace Glosify.Services.Avatar;

public sealed record AvatarTranscriptTurn(string TurnId, int Sequence, string Role, string Speaker, string Text, DateTimeOffset CapturedAt);
public sealed record AvatarTranscriptSnapshot(Guid ConversationId, string Language, string Character, AvatarTranscriptTurn[] Turns);
public sealed record AvatarTranscriptStatus(Guid ConversationId, int TurnCount, Guid? TranscriptId, int SavedTurns, bool AutoSave, bool Suppressed, long Revision = 0);

public sealed partial class AvatarSessions
{
    public const int MaximumTranscriptTurns = 2000;
    public const int MaximumTranscriptCharacters = 500_000;
    public const int MaximumRetainedTranscriptCharacters = 8_000_000;

    public void CheckTranscriptCapacity(AvatarSession session)
    {
        lock (_gate)
        {
            var memory = OwnedMemory(session.MemoryId, session.UserId, session.Language.Code);
            if (memory.WriterId != session.Id) throw new AvatarException(409, "This conversation continued in another session.");
            // Leave room for the next bounded utterance and its reply.
            // Reserve room for an utterance/reply on every possible live session.
            ReclaimTranscriptMemory(MaximumLiveSessions * 24_000, session.MemoryId);
            if (_memories.Values.Sum(m => m.Characters) > MaximumRetainedTranscriptCharacters - MaximumLiveSessions * 24_000)
                throw new AvatarException(429, "Conversation storage is full. Save and start a new conversation to free space.");
            if (memory.Turns.Count > MaximumTranscriptTurns - 2 || memory.Characters > MaximumTranscriptCharacters - 24_000)
                throw new AvatarException(422, "This conversation is full. Save it, then start a new conversation.");
        }
    }

    public void RecordTurn(AvatarSession session, string turnId, string role, string text)
    {
        lock (_gate)
        {
            var memory = OwnedMemory(session.MemoryId, session.UserId, session.Language.Code);
            if (memory.WriterId != session.Id) return; // A late old socket cannot add turns.
            if (memory.Turns.Any(t => t.TurnId == turnId && t.Role == role)) return;
            if (role is not ("user" or "assistant") || text.Length > 12_000 || string.IsNullOrWhiteSpace(text))
                throw new AvatarException(422, "The spoken turn could not be recorded. Please use a shorter utterance.");
            if (memory.Turns.Count >= MaximumTranscriptTurns || memory.Characters + text.Length > MaximumTranscriptCharacters)
                throw new AvatarException(422, "This conversation is full. Save it, then start a new conversation.");
            ReclaimTranscriptMemory(text.Length, session.MemoryId);
            if (_memories.Values.Sum(m => m.Characters) + text.Length > MaximumRetainedTranscriptCharacters)
                throw new AvatarException(429, "Conversation storage is full. Save and start a new conversation to free space.");
            memory.Turns.Add(new(turnId, memory.Turns.Count + 1, role, role == "user" ? "You" : session.Character.Name, text, clock.GetUtcNow()));
            memory.Characters += text.Length;
            memory.StatusRevision++;
            _memories[session.MemoryId] = memory with { ExpiresAt = clock.GetUtcNow().Add(MemoryLifetime) };
        }
    }

    // Called under _gate. Fully saved idle archives go first without losing transcript text.
    // Then the account retaining the most text loses its oldest idle conversation, so other
    // accounts cannot fill the budget against anyone. Connected conversations always stay.
    private void ReclaimTranscriptMemory(int reserveCharacters, Guid protectedMemory)
    {
        var excess = _memories.Values.Sum(m => m.Characters) + reserveCharacters - MaximumRetainedTranscriptCharacters;
        while (excess > 0)
        {
            var candidate = ReclaimOrder(m => m.Characters, protectedMemory: protectedMemory).FirstOrDefault(m => m.Value.Characters > 0);
            if (candidate.Value is null) return;
            _memories.Remove(candidate.Key); excess -= candidate.Value.Characters;
        }
    }

    public AvatarTranscriptSnapshot Snapshot(Guid id, string userId, string language)
    {
        lock (_gate)
        {
            var memory = OwnedMemory(id, userId, language);
            return new(id, memory.Language, memory.Character, memory.Turns.ToArray());
        }
    }

    public (Guid? Id, bool Suppressed) SavedTranscript(Guid id, string userId, string language)
    {
        lock (_gate) { var memory = OwnedMemory(id, userId, language); return (memory.TranscriptId, memory.SavingSuppressed); }
    }

    public void SetSavedTranscript(Guid id, string userId, Guid? transcriptId, bool suppressed, int savedTurns = 0)
    {
        lock (_gate)
        {
            if (!_memories.TryGetValue(id, out var memory) || memory.UserId != userId) return;
            if (memory.TranscriptId != transcriptId || memory.SavingSuppressed != suppressed || memory.SavedTurns != savedTurns)
                memory.StatusRevision++;
            memory.TranscriptId = transcriptId; memory.SavingSuppressed = suppressed; memory.SavedTurns = savedTurns;
        }
    }

    private Memory OwnedMemory(Guid id, string userId, string language)
    {
        if (!_memories.TryGetValue(id, out var memory) || memory.UserId != userId || memory.Language != language)
            throw new AvatarException(404, "Conversation not found for this learning language.");
        if (memory.ExpiresAt <= clock.GetUtcNow()) throw new AvatarException(410, "This unsaved conversation has expired.");
        return memory;
    }
}
