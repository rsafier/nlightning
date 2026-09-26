namespace NLightning.Application.Onchain.Resolvers.Remote;

using Domain.Channels.Commitments.Events;
using Domain.Onchain.Models;

/// <summary>
/// What one run of <see cref="IRemoteCommitResolver"/> decided, for the caller to finish after its save (BOLT 5 plan
/// §3.2 step 4, D4: persist before broadcast; switch events after the save, outside the channel lock).
/// </summary>
/// <remarks>
/// Every row, watch and broadcast listed here is already staged on the caller's unit of work. The caller saves,
/// releases the channel lock, then calls <see cref="IRemoteCommitResolver.CompleteAsync"/> with this round.
/// </remarks>
public sealed class RemoteResolutionRound
{
    /// <summary>Resolution outputs watched for spends in this round (staged; tracked by
    /// <see cref="IRemoteCommitResolver.CompleteAsync"/>).</summary>
    public List<WatchedOutpointModel> Watches { get; } = [];

    /// <summary>Sweeps and claims staged for broadcast in this round (published after the save).</summary>
    public List<BroadcastTransactionModel> Broadcasts { get; } = [];

    /// <summary>Upstream events for HTLCs we offered: <see cref="OutgoingHtlcFulfilled"/> and
    /// <see cref="OutgoingHtlcFailed"/> (on-chain timeout), raised to the switch after the save.</summary>
    public List<IChannelDomainEvent> SwitchEvents { get; } = [];

    /// <summary>Funds we could not (or can no longer) recover, each with its BOLT 5 plan requirement id.</summary>
    public List<string> Alerts { get; } = [];

    /// <summary>
    /// True when the peer's commitment is one we cannot rebuild (B5-RMT-03: a future commitment after data loss, or a
    /// channel without a commitment snapshot): only <c>to_remote</c> is swept and the user must be told that the other
    /// funds (HTLCs) may be lost. A CRITICAL alert, for the IPC flag.
    /// </summary>
    public bool CriticalDataLoss { get; set; }

    /// <summary>True when every output of the commitment is irrevocably resolved (100 blocks) or ignored: the channel
    /// can become <c>Closed</c> (O6-T2).</summary>
    public bool AllIrrevocablyResolved { get; set; }

    /// <summary>The rows written in this round, after their change (for tests and <c>PendingSweeps</c>).</summary>
    public List<OutputResolutionModel> Outputs { get; } = [];
}