namespace NLightning.Domain.Channels.KeyIndexes;

/// <summary>
/// The one gate in front of every new channel key index (NL-1392). Every signer hands out a new channel's key index
/// through <see cref="Allocate"/>, so nothing that opens a channel (an inbound <c>open_channel</c>/<c>open_channel2</c>,
/// our <c>openchannel</c> or dual-funded open, LND gRPC's <c>OpenChannel</c>, a liquidity sale at the open) can get one
/// while the gate is held: during a <c>restorechanbackup</c> (<see cref="BeginRestore"/>), whose channels' indexes come
/// from another database, and, opt-in, on a node set up on a fresh database until a restore completed or the operator
/// lifted the hold (<see cref="HoldUntilRestore"/>, <c>Node:Backup:HoldOpensUntilRestore</c>).
/// </summary>
/// <remarks>
/// The open paths also check <see cref="HoldReason"/> first, for a clear early refusal (an <c>error</c> to the peer, a
/// failed IPC or gRPC call); the allocation check is the one nothing can bypass. A refusal is retryable: the hold ends
/// with the restore. The gate also remembers every index this process handed out, so a restore can refuse a backed-up
/// channel whose index an open of this process already took (<see cref="IsAllocatedByThisProcess"/>).
/// </remarks>
public interface IChannelKeyIndexGate
{
    /// <summary>Why new channel key indexes are refused right now, or null when they are allowed.</summary>
    string? HoldReason { get; }

    /// <summary>True while the opt-in start hold waits for a restore (or the operator).</summary>
    bool IsAwaitingRestore { get; }

    /// <summary>True while a restore runs.</summary>
    bool IsRestoreInProgress { get; }

    /// <summary>
    /// Runs <paramref name="allocate"/>, which hands out one new channel key index, unless the gate is held, and records
    /// the index. Allocations run one at a time against <see cref="BeginRestore"/>: a restore never starts while one
    /// runs, and none starts while a restore holds the gate.
    /// </summary>
    /// <exception cref="ChannelKeyIndexHeldException">The gate is held; nothing was allocated.</exception>
    uint Allocate(Func<uint> allocate);

    /// <summary>
    /// Holds the gate for a restore until the returned handle is disposed: every new allocation is refused at once, and
    /// the task completes once an allocation that was already running has ended (awaited, never a blocking wait).
    /// Restores nest (a counter). The restore releases it as soon as its key index raise and conflict check are done.
    /// </summary>
    Task<IDisposable> BeginRestoreAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the start hold of <see cref="HoldUntilRestore"/> because a restore raised the key indexes of the backup
    /// (committed). Nothing when the hold is not on.
    /// </summary>
    void CompleteRestore();

    /// <summary>Starts the opt-in hold: opens are refused until a restore completes or the operator lifts it.</summary>
    void HoldUntilRestore();

    /// <summary>The operator lifts the start hold; true when it was on. A restore in progress still holds the gate.
    /// </summary>
    bool ReleaseRestoreHold();

    /// <summary>Whether this process handed out <paramref name="index"/> through <see cref="Allocate"/>.</summary>
    bool IsAllocatedByThisProcess(uint index);
}