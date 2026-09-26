namespace NLightning.Application.Gossip.Graph;

using Domain.Gossip.Graph;
using Interfaces;
using Sync;

/// <summary>
/// The state of the gossip graph for <c>describegraph</c> (BOLT 7 plan G5-T4, ClientCommand 20): counts from one
/// snapshot, the store's memory estimate and pending writes, the ingress queue, dropped messages and orphans, and each
/// connection's sync state. Read only; the ingress and the sync manager are optional (a node without them reports
/// null for their parts).
/// </summary>
public sealed class GossipGraphDescriber
{
    private readonly IGraphStore _store;
    private readonly GossipIngress? _ingress;
    private readonly GossipSyncManager? _syncManager;

    public GossipGraphDescriber(IGraphStore store, GossipIngress? ingress = null,
                                GossipSyncManager? syncManager = null)
    {
        _store = store;
        _ingress = ingress;
        _syncManager = syncManager;
    }

    /// <summary>Reads the graph's state now.</summary>
    public GraphDescription Describe()
    {
        var snapshot = _store.GetSnapshot();
        int spent = 0, unverified = 0, own = 0, withoutPolicy = 0, disabled = 0, policies = 0;
        ulong capacitySat = 0;
        foreach (var channel in snapshot.Channels)
        {
            if (channel.SpentAtHeight is not null)
                spent++;
            if (channel.Verification == Domain.Gossip.Graph.GraphChannelVerification.Unverified)
                unverified++;
            if (channel.Verification == Domain.Gossip.Graph.GraphChannelVerification.Own)
                own++;
            if (channel.Policy1 is null && channel.Policy2 is null)
                withoutPolicy++;
            foreach (var policy in (ReadOnlySpan<GraphPolicy?>)[channel.Policy1, channel.Policy2])
            {
                if (policy is null)
                    continue;

                policies++;
                if (policy.IsDisabled)
                    disabled++;
            }

            capacitySat += channel.CapacitySat ?? 0;
        }

        var ingress = _ingress is null
                          ? null
                          : new GossipIngressState(_ingress.QueuedCount, _ingress.DroppedCount,
                                                   _ingress.Orphans.Count);
        var sync = _syncManager is null
                       ? null
                       : new GossipSyncState(_syncManager.HasCompletedInitialSync, _syncManager.GetPeerStates());
        return new GraphDescription(_store.IsLoaded, snapshot, snapshot.ChannelCount, spent, unverified, own,
                                    withoutPolicy, policies, disabled, snapshot.Nodes.Count(), snapshot.NodeCount,
                                    capacitySat, _store.PendingChanges, _store.GetMemoryEstimate(), ingress, sync);
    }
}

/// <summary>What <see cref="GossipGraphDescriber.Describe"/> read.</summary>
/// <param name="IsLoaded">The store loaded the persisted graph.</param>
/// <param name="Snapshot">The snapshot the counts come from (for the listing pages).</param>
/// <param name="Channels">Channels, spent ones included.</param>
/// <param name="SpentChannels">Channels whose funding output is spent (removed 72 blocks later).</param>
/// <param name="UnverifiedChannels">Channels kept without a funding check (<c>FundingValidation = SkipUnavailable</c>).</param>
/// <param name="OwnChannels">Our own announced channels.</param>
/// <param name="ChannelsWithoutPolicy">Channels with no <c>channel_update</c> in either direction.</param>
/// <param name="Policies">Stored <c>channel_update</c> directions.</param>
/// <param name="DisabledPolicies">Directions whose <c>disable</c> bit is set.</param>
/// <param name="AnnouncedNodes">Nodes with a <c>node_announcement</c>.</param>
/// <param name="GraphNodes">Nodes the graph knows (channel ends and announced nodes).</param>
/// <param name="CapacitySat">The summed capacity of the verified channels.</param>
/// <param name="PendingWrites">Changes the write-behind has not saved yet.</param>
/// <param name="Memory">The store's memory estimate.</param>
/// <param name="Ingress">The ingress queues, or null without an ingress.</param>
/// <param name="Sync">The sync state, or null without a sync manager.</param>
public sealed record GraphDescription(
    bool IsLoaded,
    IGraphView Snapshot,
    int Channels,
    int SpentChannels,
    int UnverifiedChannels,
    int OwnChannels,
    int ChannelsWithoutPolicy,
    int Policies,
    int DisabledPolicies,
    int AnnouncedNodes,
    int GraphNodes,
    ulong CapacitySat,
    int PendingWrites,
    GraphMemoryEstimate Memory,
    GossipIngressState? Ingress,
    GossipSyncState? Sync);

/// <summary>The graph ingress's queues.</summary>
/// <param name="QueuedMessages">Messages waiting for a worker.</param>
/// <param name="DroppedMessages">Messages dropped because a queue was full, since the start.</param>
/// <param name="Orphans"><c>channel_update</c>s and <c>node_announcement</c>s waiting for their channel.</param>
public sealed record GossipIngressState(int QueuedMessages, long DroppedMessages, int Orphans);

/// <summary>The gossip sync's state.</summary>
/// <param name="HasCompletedInitialSync">A range sync with at least one peer completed.</param>
/// <param name="Peers">Each connection's sync state.</param>
public sealed record GossipSyncState(bool HasCompletedInitialSync, IReadOnlyList<GossipSyncPeerState> Peers);