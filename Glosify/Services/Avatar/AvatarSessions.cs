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
    public AvatarSession Create(string userId, QuizLanguage language, string practice, AvatarVoice? voice = null, AvatarCharacter? character = null, Guid? memoryId = null, bool preconnect = false)
    {
        lock (_gate)
        {
            Sweep();
            var previous = _sessions.Values.Where(x => x.UserId == userId).ToArray();
            if (preconnect && previous.Length > 0)
                throw new AvatarException(409, "A conversation is already open. Start speaking to replace it.");
            // Replacing this account's connection does not consume another live slot.
            if (_sessions.Count - previous.Length >= 16)
                throw new AvatarException(429, "All avatar sessions are busy. Try again shortly.");
            var session = new AvatarSession(userId, language, practice, clock.GetUtcNow(), voice, character);
            Memory? remembered = null;
            if (memoryId is { } id && _memories.TryGetValue(id, out var memory)
                && memory.UserId == userId && memory.Language == language.Code
                && memory.Character == session.Character.Id && memory.Practice == practice)
                remembered = memory;

            Guid? reclaim = null;
            if (remembered is null && _memories.Count >= 128)
            {
                // The caller's old session becomes idle only after admission succeeds.
                // Select a safe candidate now, but preserve every session on rejection.
                var active = _sessions.Values.Where(x => x.UserId != userId).Select(x => x.MemoryId).ToHashSet();
                var oldest = _memories.Where(x => !active.Contains(x.Key)
                    && x.Value.Turns.Count <= x.Value.SavedTurns).OrderBy(x => x.Value.ExpiresAt).FirstOrDefault();
                if (oldest.Value is null)
                    throw new AvatarException(429, "Conversation storage is full. Save or discard an existing conversation before starting another.");
                reclaim = oldest.Key;
            }

            // All capacity and context checks succeeded. Only an accepted replacement
            // cancels the old call or claims its archive's writer.
            foreach (var old in previous) Remove(old);
            if (remembered is not null)
            {
                session.MemoryId = memoryId!.Value;
                session.History.AddRange(remembered.History);
                _memories[session.MemoryId] = remembered with { WriterId = session.Id, ExpiresAt = clock.GetUtcNow().Add(MemoryLifetime) };
            }
            else
            {
                if (reclaim is { } reclaimed) _memories.Remove(reclaimed);
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
        if (_sessions.TryRemove(session.Id, out _)) session.Lifetime.Cancel();
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
