namespace NLightning.Domain.Channels.KeyIndexes;

/// <summary>The node's channel key index gate (NL-1392); see <see cref="IChannelKeyIndexGate"/>.</summary>
/// <remarks>
/// One lock serializes the allocations and the start of a restore: an allocation runs entirely under it (the signer's
/// key manager reservation and its database claim), so once <see cref="BeginRestore"/> returns no allocation is running
/// and every index handed out before is in <see cref="IsAllocatedByThisProcess"/>. Memory only: a restart forgets the
/// handed-out indexes (their channels, if saved, are channel rows the restore checks) and the start hold (the daemon
/// sets it again at a start that asks for it).
/// </remarks>
public sealed class ChannelKeyIndexGate : IChannelKeyIndexGate
{
    private readonly Lock _lock = new();
    private readonly HashSet<uint> _allocated = [];
    private int _restores;
    private bool _awaitingRestore;

    /// <inheritdoc />
    public string? HoldReason
    {
        get
        {
            lock (_lock)
                return GetHoldReasonLocked();
        }
    }

    /// <inheritdoc />
    public bool IsAwaitingRestore
    {
        get
        {
            lock (_lock)
                return _awaitingRestore;
        }
    }

    /// <inheritdoc />
    public bool IsRestoreInProgress
    {
        get
        {
            lock (_lock)
                return _restores > 0;
        }
    }

    /// <inheritdoc />
    public uint Allocate(Func<uint> allocate)
    {
        ArgumentNullException.ThrowIfNull(allocate);
        lock (_lock)
        {
            if (GetHoldReasonLocked() is { } reason)
                throw new ChannelKeyIndexHeldException(reason);

            var index = allocate();
            _allocated.Add(index);
            return index;
        }
    }

    /// <inheritdoc />
    public IDisposable BeginRestore()
    {
        lock (_lock)
            _restores++;

        return new RestoreHold(this);
    }

    /// <inheritdoc />
    public void CompleteRestore()
    {
        lock (_lock)
            _awaitingRestore = false;
    }

    /// <inheritdoc />
    public void HoldUntilRestore()
    {
        lock (_lock)
            _awaitingRestore = true;
    }

    /// <inheritdoc />
    public bool ReleaseRestoreHold()
    {
        lock (_lock)
        {
            var wasHeld = _awaitingRestore;
            _awaitingRestore = false;
            return wasHeld;
        }
    }

    /// <inheritdoc />
    public bool IsAllocatedByThisProcess(uint index)
    {
        lock (_lock)
            return _allocated.Contains(index);
    }

    private string? GetHoldReasonLocked() =>
        _restores > 0 ? ChannelKeyIndexHold.RestoreInProgressReason
        : _awaitingRestore ? ChannelKeyIndexHold.AwaitingRestoreReason
        : null;

    private void EndRestore()
    {
        lock (_lock)
            _restores--;
    }

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