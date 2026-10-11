namespace NLightning.Application.Channels.Services;

using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;

/// <summary>
/// One <see cref="SemaphoreSlim"/> per channel id, created on demand and dropped once nobody holds or waits for it.
/// </summary>
/// <remarks>
/// A channel opened with a temporary id is locked under that id until it gets its real id, and under the real id
/// afterwards: the per-peer inbound loop already serializes the open flow's messages, and block events only see the
/// channel once it has its real id.
/// </remarks>
public sealed class ChannelLockProvider : IChannelLockProvider
{
    private readonly Dictionary<ChannelId, LockEntry> _locks = [];
    private readonly Lock _sync = new();

    /// <summary>
    /// Number of channel ids that currently have a holder or a waiter (for tests and diagnostics).
    /// </summary>
    public int ActiveLockCount
    {
        get
        {
            lock (_sync)
                return _locks.Count;
        }
    }

    /// <inheritdoc />
    public async Task<IDisposable> AcquireAsync(ChannelId channelId, CancellationToken cancellationToken = default)
    {
        var entry = Retain(channelId);
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Forget(channelId, entry);
            throw;
        }

        return new Releaser(this, channelId, entry);
    }

    /// <inheritdoc />
    public IDisposable Acquire(ChannelId channelId)
    {
        var entry = Retain(channelId);
        try
        {
            entry.Semaphore.Wait();
        }
        catch
        {
            Forget(channelId, entry);
            throw;
        }

        return new Releaser(this, channelId, entry);
    }

    private LockEntry Retain(ChannelId channelId)
    {
        lock (_sync)
        {
            if (!_locks.TryGetValue(channelId, out var entry))
            {
                entry = new LockEntry();
                _locks.Add(channelId, entry);
            }

            entry.References++;
            return entry;
        }
    }

    private void Release(ChannelId channelId, LockEntry entry)
    {
        entry.Semaphore.Release();
        Forget(channelId, entry);
    }

    private void Forget(ChannelId channelId, LockEntry entry)
    {
        lock (_sync)
        {
            entry.References--;
            if (entry.References > 0)
                return;

            _locks.Remove(channelId);
            entry.Semaphore.Dispose();
        }
    }

    private sealed class LockEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int References { get; set; }
    }

    private sealed class Releaser : IDisposable
    {
        private readonly ChannelLockProvider _owner;
        private readonly ChannelId _channelId;
        private readonly LockEntry _entry;
        private int _released;

        public Releaser(ChannelLockProvider owner, ChannelId channelId, LockEntry entry)
        {
            _owner = owner;
            _channelId = channelId;
            _entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                _owner.Release(_channelId, _entry);
        }
    }
}