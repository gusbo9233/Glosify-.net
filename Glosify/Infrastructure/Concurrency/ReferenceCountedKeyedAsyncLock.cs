namespace Glosify.Infrastructure.Concurrency;

public sealed class ReferenceCountedKeyedAsyncLock : IKeyedAsyncLock, IDisposable
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _disposed;

    internal int ActiveKeyCount
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Entry entry;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries.Add(key, entry);
            }

            entry.ReferenceCount++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
            return new Lease(this, key, entry);
        }
        catch
        {
            ReleaseReference(key, entry);
            throw;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var entry in _entries.Values)
            {
                entry.Semaphore.Dispose();
            }
            _entries.Clear();
        }
    }

    private void Release(string key, Entry entry)
    {
        entry.Semaphore.Release();
        ReleaseReference(key, entry);
    }

    private void ReleaseReference(string key, Entry entry)
    {
        lock (_sync)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0
                && _entries.TryGetValue(key, out var current)
                && ReferenceEquals(entry, current))
            {
                _entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
    }

    private sealed class Lease(
        ReferenceCountedKeyedAsyncLock owner,
        string key,
        Entry entry) : IAsyncDisposable
    {
        private ReferenceCountedKeyedAsyncLock? _owner = owner;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(key, entry);
            return ValueTask.CompletedTask;
        }
    }
}
