using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Speaking;

public interface ISpeakingSessionStore
{
    /// <summary>
    /// Drops every session past its TTL, across all users, disposing each conversation.
    /// </summary>
    /// <returns>How many sessions were removed.</returns>
    Task<int> RemoveAllExpiredAsync(CancellationToken cancellationToken = default);

    Task<SpeakingSessionState> CreateAsync(
        string userId,
        SpeakingAvatarDefinition avatar,
        CefrLevel cefrLevel,
        bool interactiveMode = false,
        CancellationToken cancellationToken = default,
        SpeakingQuizContextState? quizContext = null);

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
    private readonly ILogger<SpeakingSessionStore> _logger;
    private readonly TimeSpan _ttl;
    private readonly int _maxSessionsPerUser;

    public SpeakingSessionStore(
        ISpeakingAgentClient agentClient,
        IOptions<SpeakingOptions> options,
        TimeProvider timeProvider,
        ILogger<SpeakingSessionStore>? logger = null)
    {
        _agentClient = agentClient;
        _timeProvider = timeProvider;
        _logger = logger ?? NullLogger<SpeakingSessionStore>.Instance;
        _ttl = TimeSpan.FromMinutes(Math.Clamp(options.Value.SessionTtlMinutes, 5, 240));
        _maxSessionsPerUser = Math.Clamp(options.Value.MaxSessionsPerUser, 1, 10);
    }

    public async Task<SpeakingSessionState> CreateAsync(
        string userId,
        SpeakingAvatarDefinition avatar,
        CefrLevel cefrLevel,
        bool interactiveMode = false,
        CancellationToken cancellationToken = default,
        SpeakingQuizContextState? quizContext = null)
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
            _ttl,
            quizContext);

        // Decide under the lock, but dispose outside it: DisposeAsync reaches Foundry to
        // tear down the remote conversation, and awaiting that is not possible inside a
        // lock. Discarding the ValueTask instead would leave the remote conversation to
        // expire on its own and swallow any fault.
        bool atCapacity;
        lock (_mutationLock)
        {
            atCapacity = CountForUser(userId) >= _maxSessionsPerUser;
            if (!atCapacity)
            {
                _sessions[state.Id] = state;
            }
        }

        if (atCapacity)
        {
            await conversation.DisposeAsync();
            throw new SpeakingSessionLimitException(_maxSessionsPerUser);
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
            DisposeDetached(state.Conversation);
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

    /// <summary>
    /// Disposes a conversation without awaiting it, for the synchronous <see cref="Get"/> path
    /// where awaiting is not an option. <c>AsTask</c> consumes the <see cref="ValueTask"/> exactly
    /// once — it must never be consumed twice — and the continuation observes any fault so a
    /// failed Foundry teardown cannot resurface later as an unobserved task exception.
    /// </summary>
    private static void DisposeDetached(ISpeakingAgentConversation conversation) =>
        _ = conversation.DisposeAsync().AsTask().ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

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

    private Task RemoveExpiredAsync(string userId) =>
        RemoveExpiredWhereAsync(session => string.Equals(session.UserId, userId, StringComparison.Ordinal));

    /// <inheritdoc />
    public async Task<int> RemoveAllExpiredAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await RemoveExpiredWhereAsync(_ => true);
    }

    private async Task<int> RemoveExpiredWhereAsync(Func<SpeakingSessionState, bool> predicate)
    {
        var now = _timeProvider.GetUtcNow();
        var expired = _sessions.Values
            .Where(session => predicate(session) && session.IsExpired(now))
            .ToArray();

        var removed = 0;
        foreach (var state in expired)
        {
            if (_sessions.TryRemove(state.Id, out _))
            {
                removed++;
                // Best effort: one conversation failing to tear down on the Foundry side
                // must not stop the sweep from reaping the rest.
                try
                {
                    await state.Conversation.DisposeAsync();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        exception,
                        "Could not dispose the Foundry conversation for expired speaking session {SessionId}",
                        state.Id);
                }
            }
        }

        return removed;
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
    TimeSpan ttl,
    SpeakingQuizContextState? quizContext = null)
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
    public SpeakingQuizContextState? QuizContext { get; set; } = quizContext;
    public SpeakingPracticePrompt? PendingPracticePrompt { get; set; }
    public bool HasTransactionalTools => InteractiveMode || QuizContext is not null;
    public SemaphoreSlim TurnGate { get; } = new(1, 1);

    public bool IsExpired(DateTimeOffset at) =>
        at - new DateTimeOffset(Interlocked.Read(ref _lastAccessUtcTicks), TimeSpan.Zero) > ttl;

    public void Touch(DateTimeOffset at) =>
        Interlocked.Exchange(ref _lastAccessUtcTicks, at.UtcTicks);
}
