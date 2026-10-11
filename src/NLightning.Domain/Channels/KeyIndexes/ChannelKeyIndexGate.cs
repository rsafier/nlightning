using System.Collections.Concurrent;

namespace NLightning.Domain.Channels.KeyIndexes;

/// <summary>The node's channel key index gate (NL-1392); see <see cref="IChannelKeyIndexGate"/>.</summary>
/// <remarks>
/// The hold state is plain fields read with <see cref="Volatile"/>, so <see cref="HoldReason"/> never waits for a slow
/// signer. Allocations run one at a time under a separate allocation semaphore (the signer's key manager reservation and
/// its database claim run inside it) and check the hold under it; <see cref="BeginRestoreAsync"/> raises the restore
/// counter first and then awaits that semaphore once, so when it returns no allocation is running, none can start, and
/// every index handed out before is in <see cref="IsAllocatedByThisProcess"/>. Memory only: a restart forgets the
/// handed-out indexes (their channels, if saved, are channel rows; the native remote signer's unconsumed allocations
/// are read from their workflows) and the start hold (the daemon sets it again at a start on an empty database).
/// </remarks>
public sealed class ChannelKeyIndexGate : IChannelKeyIndexGate
{
    private readonly SemaphoreSlim _allocation = new(1, 1);
    private readonly ConcurrentDictionary<uint, byte> _allocated = new();
    private int _restores;
    private int _awaitingRestore;

    /// <inheritdoc />
    public string? HoldReason =>
        Volatile.Read(ref _restores) > 0 ? ChannelKeyIndexHold.RestoreInProgressReason
        : Volatile.Read(ref _awaitingRestore) != 0 ? ChannelKeyIndexHold.AwaitingRestoreReason
        : null;

    /// <inheritdoc />
    public bool IsAwaitingRestore => Volatile.Read(ref _awaitingRestore) != 0;

    /// <inheritdoc />
    public bool IsRestoreInProgress => Volatile.Read(ref _restores) > 0;

    /// <inheritdoc />
    public uint Allocate(Func<uint> allocate)
    {
        ArgumentNullException.ThrowIfNull(allocate);

        // Refused before waiting, and again under the semaphore: a restore may have begun meanwhile
        if (HoldReason is { } early)
            throw new ChannelKeyIndexHeldException(early);

        _allocation.Wait();
        try
        {
            if (HoldReason is { } reason)
                throw new ChannelKeyIndexHeldException(reason);

            var index = allocate();
            _allocated.TryAdd(index, 0);
            return index;
        }
        finally
        {
            _allocation.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IDisposable> BeginRestoreAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _restores);
        var hold = new RestoreHold(this);
        try
        {
            // A barrier: an allocation that was running when the counter rose has ended once this is acquired
            await _allocation.WaitAsync(cancellationToken).ConfigureAwait(false);
            _allocation.Release();
            return hold;
        }
        catch
        {
            hold.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void CompleteRestore() => Volatile.Write(ref _awaitingRestore, 0);

    /// <inheritdoc />
    public void HoldUntilRestore() => Volatile.Write(ref _awaitingRestore, 1);

    /// <inheritdoc />
    public bool ReleaseRestoreHold() => Interlocked.Exchange(ref _awaitingRestore, 0) != 0;

    /// <inheritdoc />
    public bool IsAllocatedByThisProcess(uint index) => _allocated.ContainsKey(index);

    private void EndRestore() => Interlocked.Decrement(ref _restores);

    private sealed class RestoreHold(ChannelKeyIndexGate gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                gate.EndRestore();
        }
    }
}