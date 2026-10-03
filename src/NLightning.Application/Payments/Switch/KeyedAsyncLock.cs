namespace NLightning.Application.Payments.Switch;

/// <summary>
/// One non-reentrant asynchronous lock per key, created on first use and dropped when nobody holds or waits for it.
/// </summary>
/// <typeparam name="TKey">The key type (an incoming HTLC, a payment hash).</typeparam>
internal sealed class KeyedAsyncLock<TKey> where TKey : notnull
{
    private readonly Dictionary<TKey, Entry> _entries = [];

    /// <summary>The number of keys currently locked or waited for (tests).</summary>
    public int Count
    {
        get
        {
            lock (_entries)
                return _entries.Count;
        }
    }

    /// <summary>
    /// Waits for the lock of <paramref name="key"/>; dispose the result to release it.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(TKey key, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.Users++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            Leave(key, entry);
            throw;
        }

        return new Releaser(this, key, entry);
    }

    private void Leave(TKey key, Entry entry)
    {
        lock (_entries)
        {
            if (--entry.Users == 0)
                _entries.Remove(key);
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Users { get; set; }
    }

    private sealed class Releaser(KeyedAsyncLock<TKey> owner, TKey key, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            entry.Semaphore.Release();
            owner.Leave(key, entry);
        }
    }
}