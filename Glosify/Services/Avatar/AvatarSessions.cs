using System.Collections.Concurrent;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Language;

namespace Glosify.Services.Avatar;

public sealed class AvatarSession(string userId, QuizLanguage language, string practice, DateTimeOffset now, AvatarVoice? voice = null)
{
    public Guid Id { get; } = Guid.NewGuid();
    public string UserId { get; } = userId;
    public QuizLanguage Language { get; } = language;
    public string Practice { get; } = practice;
    public AvatarVoice? Voice { get; } = voice;
    public DateTimeOffset ExpiresAt { get; } = now.AddMinutes(30);
    public DateTimeOffset ConnectBy { get; } = now.AddSeconds(30);
    public CancellationTokenSource Lifetime { get; } = new();
    public List<AgentTurn> History { get; } = [];
    public int Connected;
}

public sealed class AvatarSessions(TimeProvider clock)
{
    private readonly ConcurrentDictionary<Guid, AvatarSession> _sessions = new();
    private readonly object _gate = new();
    public AvatarSession Create(string userId, QuizLanguage language, string practice, AvatarVoice? voice = null)
    {
        lock (_gate)
        {
            Sweep();
            if (_sessions.Values.Any(x => x.UserId == userId)) throw new AvatarException(409, "End the other avatar session before starting another.");
            if (_sessions.Count >= 16) throw new AvatarException(429, "All avatar sessions are busy. Try again shortly.");
            var session = new AvatarSession(userId, language, practice, clock.GetUtcNow(), voice);
            _sessions[session.Id] = session;
            return session;
        }
    }
    public AvatarSession Get(Guid id, string userId)
    {
        if (!_sessions.TryGetValue(id, out var session) || session.UserId != userId) throw new AvatarException(404, "Conversation not found.");
        if (session.ExpiresAt <= clock.GetUtcNow() || session.Connected == 0 && session.ConnectBy <= clock.GetUtcNow())
        { Remove(session); throw new AvatarException(410, "Conversation expired. Start a new conversation."); }
        return session;
    }
    public void Remove(AvatarSession session)
    {
        if (_sessions.TryRemove(session.Id, out _)) session.Lifetime.Cancel();
    }
    public void Sweep()
    {
        foreach (var session in _sessions.Values)
            if (session.ExpiresAt <= clock.GetUtcNow() || session.Connected == 0 && session.ConnectBy <= clock.GetUtcNow()) Remove(session);
    }
}

public sealed class AvatarException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
