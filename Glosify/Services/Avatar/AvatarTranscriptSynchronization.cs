namespace Glosify.Services.Avatar;

public sealed partial class AvatarSessions
{
    private readonly Dictionary<Guid, int> _retainedTranscripts = [];

    // Capacity reclamation must not remove an archive between its database write
    // and the status returned to the caller. The lease also releases on failure.
    public IDisposable RetainTranscript(Guid id, string userId, string language)
    {
        lock (_gate)
        {
            OwnedMemory(id, userId, language);
            _retainedTranscripts[id] = _retainedTranscripts.GetValueOrDefault(id) + 1;
        }
        return new TranscriptWriteLease(() =>
        {
            lock (_gate)
            {
                if (--_retainedTranscripts[id] == 0) _retainedTranscripts.Remove(id);
            }
        });
    }

    private readonly object _transcriptGate = new();
    private readonly Dictionary<string, TranscriptWriteLock> _transcriptWrites = [];
    private sealed class TranscriptWriteLock
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int References { get; set; }
    }

    // Serialize each owner's saves, preference changes and avatar deletes. Unrelated
    // accounts never queue behind each other; locks disappear after the last waiter.
    public async ValueTask<IDisposable> LockTranscriptWritesAsync(string userId, CancellationToken ct)
    {
        TranscriptWriteLock entry;
        lock (_transcriptGate)
        {
            if (!_transcriptWrites.TryGetValue(userId, out entry!))
                _transcriptWrites[userId] = entry = new();
            entry.References++;
        }
        try { await entry.Semaphore.WaitAsync(ct); }
        catch { ReleaseReference(); throw; }
        return new TranscriptWriteLease(() => { entry.Semaphore.Release(); ReleaseReference(); });

        void ReleaseReference()
        {
            lock (_transcriptGate)
            {
                if (--entry.References != 0) return;
                _transcriptWrites.Remove(userId);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class TranscriptWriteLease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    public bool? AutoSavePreference(Guid id, string userId, string language)
    {
        lock (_gate) { return OwnedMemory(id, userId, language).AutoSaveConversations; }
    }

    public void SetAutoSavePreference(string userId, bool enabled)
    {
        lock (_gate)
            foreach (var memory in _memories.Values.Where(m => m.UserId == userId))
            {
                if (memory.AutoSaveConversations != enabled) memory.StatusRevision++;
                memory.AutoSaveConversations = enabled;
            }
    }

    public AvatarTranscriptStatus CachedTranscriptStatus(Guid id, string userId, string language)
    {
        lock (_gate)
        {
            var memory = OwnedMemory(id, userId, language);
            return new(id, memory.Turns.Count, memory.TranscriptId, memory.SavedTurns,
                memory.AutoSaveConversations == true, memory.SavingSuppressed, memory.StatusRevision);
        }
    }
}
