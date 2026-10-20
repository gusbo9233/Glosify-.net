using System.Collections.Concurrent;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Language;

namespace Glosify.Services.Avatar;

public sealed class AvatarSession(string userId, QuizLanguage language, string practice, DateTimeOffset now, AvatarVoice? voice = null, AvatarCharacter? character = null)
{
    public Guid Id { get; } = Guid.NewGuid();
    public Guid MemoryId { get; internal set; } = Guid.NewGuid();
    public string UserId { get; } = userId;
    public QuizLanguage Language { get; } = language;
    public string Practice { get; } = practice;
    public AvatarCharacter Character { get; } = character ?? AvatarCharacter.Rain;
    public AvatarVoice? Voice { get; } = voice;
    public DateTimeOffset ExpiresAt { get; } = now.AddMinutes(30);
    public DateTimeOffset ConnectBy { get; internal set; } = now.AddSeconds(30);
    public CancellationTokenSource Lifetime { get; } = new();
    public List<AgentTurn> History { get; } = [];
    public int Connected;
    public bool HasStartedTurn { get; internal set; }
    internal bool OwnsPreparationMemory { get; set; }
    // Cancelled when a full pool gives this idle session's slot to another learner.
    internal CancellationTokenSource Preemption { get; } = new();
    // Starts at creation, so it orders preemption without protecting the new session.
    private long _keptUntilTicks = now.UtcTicks;
    // Only billed conversation keeps a live slot from being preempted; heartbeats,
    // reconnects and empty listens do not.
    internal DateTimeOffset KeptUntil => new(Interlocked.Read(ref _keptUntilTicks), TimeSpan.Zero);
    internal void KeepActiveUntil(DateTimeOffset until)
    {
        var ticks = until.UtcTicks;
        for (var current = Interlocked.Read(ref _keptUntilTicks); current < ticks;)
        {
            var seen = Interlocked.CompareExchange(ref _keptUntilTicks, ticks, current);
            if (seen == current) return;
            current = seen;
        }
    }
}

public sealed partial class AvatarSessions(TimeProvider clock)
{
    private readonly ConcurrentDictionary<Guid, AvatarSession> _sessions = new();
    private readonly object _gate = new();
    private sealed record Memory(string UserId, string Language, string Character, string Practice,
        Guid WriterId, DateTimeOffset ExpiresAt, AgentTurn[] History)
    {
        public List<AvatarTranscriptTurn> Turns { get; init; } = [];
        public int Characters { get; set; }
        public Guid? TranscriptId { get; set; }
        public bool SavingSuppressed { get; set; }
        public int SavedTurns { get; set; }
        public long StatusRevision { get; set; }
        public bool? AutoSaveConversations { get; set; }
    }
    private readonly Dictionary<Guid, Memory> _memories = [];
    private static readonly TimeSpan MemoryLifetime = TimeSpan.FromHours(8);
    public const int MaximumLiveSessions = 16;
    public const int MaximumConversations = 128;
    // One live session per account, plus a few kept conversations from other tabs or partners.
    public const int MaximumConversationsPerUser = 4;
    private int _preparations;
    // Bounds idle provider connections held for a faster first turn.
    public bool TryReservePreparation(int limit)
    {
        lock (_gate)
        {
            if (_preparations >= limit) return false;
            _preparations++; return true;
        }
    }
    public void ReleasePreparation() { lock (_gate) _preparations--; }
    public AvatarSession Create(string userId, QuizLanguage language, string practice, AvatarVoice? voice = null, AvatarCharacter? character = null, Guid? memoryId = null, bool preconnect = false)
    {
        lock (_gate)
        {
            Sweep();
            var previous = _sessions.Values.Where(x => x.UserId == userId).ToArray();
            if (preconnect && previous.Length > 0)
                throw new AvatarException(409, "A conversation is already open. Start speaking to replace it.");
            // Replacing this account's connection does not consume another live slot. When
            // every slot is taken, the session longest without billed conversation yields;
            // a speculative preparation never displaces anyone.
            var others = _sessions.Values.Where(x => x.UserId != userId && !x.Preemption.IsCancellationRequested).ToArray();
            AvatarSession? displaced = null;
            if (others.Length >= MaximumLiveSessions)
            {
                var now = clock.GetUtcNow();
                displaced = preconnect ? null : others.Where(x => x.KeptUntil <= now).MinBy(x => x.KeptUntil);
                if (displaced is null) throw new AvatarException(429, "All avatar sessions are busy. Try again shortly.");
            }
            var session = new AvatarSession(userId, language, practice, clock.GetUtcNow(), voice, character);
            Memory? remembered = null;
            if (memoryId is { } id && _memories.TryGetValue(id, out var memory)
                && memory.UserId == userId && memory.Language == language.Code
                && memory.Character == session.Character.Id && memory.Practice == practice)
                remembered = memory;

            Guid? reclaim = null;
            // Replacing an unused preparation below already releases its empty conversation.
            var owned = remembered is null
                ? _memories.Where(x => x.Value.UserId == userId
                    && !previous.Any(p => p.MemoryId == x.Key && IsUnusedPreparation(p, x.Value))).ToArray()
                : [];
            // One account cannot fill the shared pool: past its cap it reclaims its own
            // conversations. A full shared pool never locks learners out either. Unsaved
            // turns in a reclaimed idle conversation are dropped; a live one never qualifies.
            if (owned.Length >= MaximumConversationsPerUser || remembered is null && _memories.Count >= MaximumConversations)
            {
                if (preconnect) throw new AvatarException(429, "Conversation storage is full. Start speaking to create a conversation.");
                var candidates = ReclaimOrder(_ => 1, owned.Length >= MaximumConversationsPerUser ? userId : null);
                if (candidates.Length == 0)
                    throw new AvatarException(429, "Conversation storage is full. Save or discard an existing conversation before starting another.");
                reclaim = candidates[0].Key;
            }

            // All capacity and context checks succeeded. Only an accepted replacement
            // cancels the old call, claims its archive's writer, or displaces another session.
            foreach (var old in previous) Remove(old);
            if (displaced is not null) Preempt(displaced);
            if (remembered is not null)
            {
                session.MemoryId = memoryId!.Value;
                session.History.AddRange(remembered.History);
                _memories[session.MemoryId] = remembered with { WriterId = session.Id, ExpiresAt = clock.GetUtcNow().Add(MemoryLifetime) };
            }
            else
            {
                if (reclaim is { } reclaimed) _memories.Remove(reclaimed);
                session.OwnsPreparationMemory = preconnect;
                _memories[session.MemoryId] = new(userId, language.Code, session.Character.Id, practice, session.Id,
                    clock.GetUtcNow().Add(MemoryLifetime), []);
            }
            _sessions[session.Id] = session;
            return session;
        }
    }
    public AvatarSession Get(Guid id, string userId)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out var session) || session.UserId != userId) throw new AvatarException(404, "Conversation not found.");
            if (session.Preemption.IsCancellationRequested) throw new AvatarException(410, "Conversation ended. Start a new conversation.");
            if (session.ExpiresAt <= clock.GetUtcNow() || session.Connected == 0 && session.ConnectBy <= clock.GetUtcNow())
            { Remove(session); throw new AvatarException(410, "Conversation expired. Start a new conversation."); }
            return session;
        }
    }
    public AvatarSession Connect(Guid id, string userId)
    {
        lock (_gate)
        {
            var session = Get(id, userId);
            if (session.Connected != 0) throw new AvatarException(409, "This conversation is already connected.");
            session.Connected = 1;
            return session;
        }
    }
    public void Disconnect(AvatarSession session)
    {
        lock (_gate)
        {
            if (!_sessions.ContainsKey(session.Id)) return;
            session.ConnectBy = clock.GetUtcNow().AddSeconds(45);
            session.Connected = 0;
        }
    }
    public void Remove(AvatarSession session)
    {
        lock (_gate)
        {
            if (!_sessions.TryRemove(session.Id, out _)) return;
            session.Lifetime.Cancel();
            if (_memories.TryGetValue(session.MemoryId, out var memory) && IsUnusedPreparation(session, memory))
                _memories.Remove(session.MemoryId);
        }
    }
    // An unused preparation's empty conversation is released when its session ends.
    private static bool IsUnusedPreparation(AvatarSession session, Memory memory) =>
        session.OwnsPreparationMemory && !session.HasStartedTurn && memory.WriterId == session.Id
        && memory.Turns.Count == 0 && memory.History.Length == 0 && session.History.Count == 0
        && memory.TranscriptId is null && memory.SavedTurns == 0;
    // Called under _gate. A connected session first tells its learner why it ends; its
    // conversation stays resumable. A disconnected one is removed at once.
    private void Preempt(AvatarSession session)
    {
        if (Volatile.Read(ref session.Connected) == 0) Remove(session);
        else session.Preemption.Cancel();
    }
    // Called under _gate. Idle conversations in reclaim order: fully saved ones first, as no
    // text is lost, then unsaved ones from the account holding the most, so neither one account
    // nor a group of accounts can crowd others out. Live conversations never qualify.
    private KeyValuePair<Guid, Memory>[] ReclaimOrder(Func<Memory, long> weight, string? owner = null, Guid? protectedMemory = null)
    {
        var live = _sessions.Values.Select(x => x.MemoryId).ToHashSet();
        var held = _memories.Values.GroupBy(x => x.UserId).ToDictionary(x => x.Key, x => x.Sum(weight));
        return _memories.Where(x => !live.Contains(x.Key) && x.Key != protectedMemory && (owner is null || x.Value.UserId == owner))
            .OrderBy(x => x.Value.Turns.Count > x.Value.SavedTurns)
            .ThenByDescending(x => x.Value.Turns.Count > x.Value.SavedTurns ? held[x.Value.UserId] : 0)
            .ThenBy(x => x.Value.ExpiresAt).ToArray();
    }
    // Called by the conversation's single history writer. Snapshots never share
    // a mutable list with a new connection, and only acknowledged replies are saved.
    public void Remember(AvatarSession session)
    {
        lock (_gate)
        {
            if (!_memories.TryGetValue(session.MemoryId, out var memory)
                || memory.WriterId != session.Id) return; // Forgotten, evicted, or superseded by another visit.
            var history = session.History.TakeLast(24).ToList();
            while (history.Sum(x => x.ContentJson.Length) > 24000) history.RemoveAt(0);
            _memories[session.MemoryId] = memory with { History = history.ToArray(), ExpiresAt = clock.GetUtcNow().Add(MemoryLifetime) };
        }
    }
    public void Forget(Guid memoryId, string userId)
    {
        lock (_gate)
        {
            if (!_memories.TryGetValue(memoryId, out var memory) || memory.UserId != userId) return;
            _memories.Remove(memoryId);
            foreach (var session in _sessions.Values.Where(x => x.MemoryId == memoryId)) Remove(session);
        }
    }
    public void Sweep()
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            foreach (var session in _sessions.Values)
                if (session.ExpiresAt <= now || session.Connected == 0 && session.ConnectBy <= now) Remove(session);
            foreach (var id in _memories.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key).ToArray())
                _memories.Remove(id);
        }
    }
}

public sealed class AvatarException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
