using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Speaking;

public interface ISpeakingSessionStore
{
    Task<SpeakingSessionState> CreateAsync(
        string userId,
        SpeakingAvatarDefinition avatar,
        CefrLevel cefrLevel,
        bool interactiveMode = false,
        CancellationToken cancellationToken = default);

    SpeakingSessionState Get(Guid sessionId, string userId);

    Task DeleteAsync(
        Guid sessionId,
        string userId,
        CancellationToken cancellationToken = default);

    Task InvalidateAsync(SpeakingSessionState session);
}

public sealed class SpeakingSessionStore : ISpeakingSessionStore
{
    private readonly ConcurrentDictionary<Guid, SpeakingSessionState> _sessions = new();
    private readonly object _mutationLock = new();
    private readonly ISpeakingAgentClient _agentClient;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ttl;
    private readonly int _maxSessionsPerUser;

    public SpeakingSessionStore(
        ISpeakingAgentClient agentClient,
        IOptions<SpeakingOptions> options,
        TimeProvider timeProvider)
    {
        _agentClient = agentClient;
        _timeProvider = timeProvider;
        _ttl = TimeSpan.FromMinutes(Math.Clamp(options.Value.SessionTtlMinutes, 5, 240));
        _maxSessionsPerUser = Math.Clamp(options.Value.MaxSessionsPerUser, 1, 10);
    }

    public async Task<SpeakingSessionState> CreateAsync(
        string userId,
        SpeakingAvatarDefinition avatar,
        CefrLevel cefrLevel,
        bool interactiveMode = false,
        CancellationToken cancellationToken = default)
    {
        await RemoveExpiredAsync(userId);
        EnsureCapacity(userId);

        var conversation = await _agentClient.CreateConversationAsync(
            avatar.Id,
            interactiveMode,
            cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var state = new SpeakingSessionState(
            Guid.NewGuid(),
            userId,
            avatar,
            cefrLevel,
            interactiveMode,
            conversation,
            now,
            _ttl);

        lock (_mutationLock)
        {
            if (CountForUser(userId) >= _maxSessionsPerUser)
            {
                _ = conversation.DisposeAsync();
                throw new SpeakingSessionLimitException(_maxSessionsPerUser);
            }

            _sessions[state.Id] = state;
        }

        return state;
    }

    public SpeakingSessionState Get(Guid sessionId, string userId)
    {
        if (!_sessions.TryGetValue(sessionId, out var state)
            || !string.Equals(state.UserId, userId, StringComparison.Ordinal))
        {
            throw new SpeakingSessionNotFoundException();
        }

        if (state.IsExpired(_timeProvider.GetUtcNow()))
        {
            _sessions.TryRemove(sessionId, out _);
            _ = state.Conversation.DisposeAsync();
            throw new SpeakingSessionExpiredException();
        }

        state.Touch(_timeProvider.GetUtcNow());
        return state;
    }

    public async Task DeleteAsync(
        Guid sessionId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(sessionId, out var state)
            || !string.Equals(state.UserId, userId, StringComparison.Ordinal))
        {
            throw new SpeakingSessionNotFoundException();
        }

        if (!_sessions.TryRemove(sessionId, out var removed))
        {
            throw new SpeakingSessionNotFoundException();
        }

        await removed.Conversation.DisposeAsync();
    }

    public async Task InvalidateAsync(SpeakingSessionState session)
    {
        if (!_sessions.TryGetValue(session.Id, out var current)
            || !ReferenceEquals(current, session)
            || !_sessions.TryRemove(session.Id, out var removed))
        {
            return;
        }

        await removed.Conversation.DisposeAsync();
    }

    private void EnsureCapacity(string userId)
    {
        lock (_mutationLock)
        {
            if (CountForUser(userId) >= _maxSessionsPerUser)
            {
                throw new SpeakingSessionLimitException(_maxSessionsPerUser);
            }
        }
    }

    private int CountForUser(string userId) =>
        _sessions.Values.Count(session =>
            string.Equals(session.UserId, userId, StringComparison.Ordinal)
            && !session.IsExpired(_timeProvider.GetUtcNow()));

    private async Task RemoveExpiredAsync(string userId)
    {
        var now = _timeProvider.GetUtcNow();
        var expired = _sessions.Values
            .Where(session =>
                string.Equals(session.UserId, userId, StringComparison.Ordinal)
                && session.IsExpired(now))
            .ToArray();

        foreach (var state in expired)
        {
            if (_sessions.TryRemove(state.Id, out _))
            {
                await state.Conversation.DisposeAsync();
            }
        }
    }
}

public sealed class SpeakingSessionState(
    Guid id,
    string userId,
    SpeakingAvatarDefinition avatar,
    CefrLevel cefrLevel,
    bool interactiveMode,
    ISpeakingAgentConversation conversation,
    DateTimeOffset now,
    TimeSpan ttl)
{
    private long _lastAccessUtcTicks = now.UtcTicks;

    public Guid Id { get; } = id;
    public string UserId { get; } = userId;
    public SpeakingAvatarDefinition Avatar { get; } = avatar;
    public CefrLevel CefrLevel { get; } = cefrLevel;
    public bool InteractiveMode { get; } = interactiveMode;
    public ISpeakingAgentConversation Conversation { get; } = conversation;
    public BartenderInteractionState? InteractionState { get; set; } =
        interactiveMode ? BartenderInteractionState.Create() : null;
    public SemaphoreSlim TurnGate { get; } = new(1, 1);

    public bool IsExpired(DateTimeOffset at) =>
        at - new DateTimeOffset(Interlocked.Read(ref _lastAccessUtcTicks), TimeSpan.Zero) > ttl;

    public void Touch(DateTimeOffset at) =>
        Interlocked.Exchange(ref _lastAccessUtcTicks, at.UtcTicks);
}
