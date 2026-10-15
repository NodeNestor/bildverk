namespace Bildverk.Internal;

/// <summary>
/// One async lock per cache key. Ten simultaneous requests for the same uncached image
/// result in one processing run; the other nine wait and then read the cache.
/// </summary>
internal sealed class AsyncKeyedLock
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public async Task<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
            }
            entry.RefCount++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            Release(key, entry, held: false);
            throw;
        }

        return new Releaser(this, key, entry);
    }

    internal int Count
    {
        get { lock (_entries) return _entries.Count; }
    }

    private void Release(string key, Entry entry, bool held)
    {
        bool last;
        lock (_entries)
        {
            last = --entry.RefCount == 0;
            if (last) _entries.Remove(key);
        }
        if (held) entry.Semaphore.Release();
        if (last) entry.Dispose(); // nobody else holds or waits on it any more
    }

    private sealed class Entry : IDisposable
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount;

        public void Dispose() => Semaphore.Dispose();
    }

    private sealed class Releaser(AsyncKeyedLock owner, string key, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(key, entry, held: true);
        }
    }
}
