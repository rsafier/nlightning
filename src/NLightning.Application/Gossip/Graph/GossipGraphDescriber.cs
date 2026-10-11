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
        int v2Channels = 0, bothVersions = 0, v2Policies = 0, v2Disabled = 0;
        ulong capacitySat = 0;
        foreach (var channel in snapshot.Channels)
        {
            if (channel.SpentAtHeight is not null)
                spent++;
            // Assumed channels (Gossip:AssumeChannelValid) are counted as unverified: no chain check either
            if (channel.Verification is Domain.Gossip.Graph.GraphChannelVerification.Unverified
                                     or Domain.Gossip.Graph.GraphChannelVerification.Assumed)
                unverified++;
            if (channel.Verification == Domain.Gossip.Graph.GraphChannelVerification.Own)
                own++;
            if (channel.Policy1 is null && channel.Policy2 is null && channel.Policy1V2 is null
             && channel.Policy2V2 is null)
                withoutPolicy++;
            foreach (var policy in (ReadOnlySpan<GraphPolicy?>)[channel.Policy1, channel.Policy2])
            {
                if (policy is null)
                    continue;

                policies++;
                if (policy.IsDisabled)
                    disabled++;
            }

            // NL-1141: taproot gossip (NL-878), counted apart from the BOLT 7 policies above
            if (channel.HasV2)
            {
                v2Channels++;
                if (channel.HasV1)
                    bothVersions++;
            }

            foreach (var policy in (ReadOnlySpan<GraphPolicy?>)[channel.Policy1V2, channel.Policy2V2])
            {
                if (policy is null)
                    continue;

                v2Policies++;
                if (policy.IsDisabled)
                    v2Disabled++;
            }

            // Only unspent channels whose capacity came from the chain (verified or our own) count
            if (channel.SpentAtHeight is null && channel.IsChainChecked)
                capacitySat += channel.CapacitySat ?? 0;
        }

        var ingress = _ingress is null
                          ? null
                          : new GossipIngressState(_ingress.QueuedCount, _ingress.DroppedCount,
                                                   _ingress.Orphans.Count, _ingress.PendingAnnouncementCount)
                          {
                              PendingAnnouncements2 = _ingress.PendingAnnouncement2Count
                          };
        var sync = _syncManager is null
                       ? null
                       : new GossipSyncState(_syncManager.HasCompletedInitialSync, _syncManager.GetPeerStates());
        int announcedNodes = 0, v2Nodes = 0;
        foreach (var node in snapshot.Nodes)
        {
            announcedNodes++;
            if (node.HasV2)
                v2Nodes++;
        }

        return new GraphDescription(_store.IsLoaded, snapshot, snapshot.ChannelCount, spent, unverified, own,
                                    withoutPolicy, policies, disabled, announcedNodes, snapshot.NodeCount,
                                    capacitySat, _store.PendingChanges, _store.GetMemoryEstimate(), ingress, sync)
        {
            V2 = new GraphV2Counts(v2Channels, bothVersions, v2Policies, v2Disabled, v2Nodes)
        };
    }
}

/// <summary>What <see cref="GossipGraphDescriber.Describe"/> read.</summary>
/// <param name="IsLoaded">The store loaded the persisted graph.</param>
/// <param name="Snapshot">The snapshot the counts come from (for the listing pages).</param>
/// <param name="Channels">Channels, spent ones included.</param>
/// <param name="SpentChannels">Channels whose funding output is spent (removed 72 blocks later).</param>
/// <param name="UnverifiedChannels">
/// Channels kept without a funding check (<c>FundingValidation = SkipUnavailable</c>, or <c>AssumeChannelValid</c>).
/// </param>
/// <param name="OwnChannels">Our own announced channels.</param>
/// <param name="ChannelsWithoutPolicy">
/// Channels with no <c>channel_update</c> nor <c>channel_update_2</c> in either direction.
/// </param>
/// <param name="Policies">Stored <c>channel_update</c> directions (BOLT 7 only; see <see cref="V2"/>).</param>
/// <param name="DisabledPolicies">Directions whose <c>disable</c> bit is set.</param>
/// <param name="AnnouncedNodes">Nodes with a <c>node_announcement</c>.</param>
/// <param name="GraphNodes">Nodes the graph knows (channel ends and announced nodes).</param>
/// <param name="CapacitySat">
/// The summed capacity of the unspent verified and own channels (spent and unverified channels are left out).
/// </param>
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
    GossipSyncState? Sync)
{
    /// <summary>The taproot gossip counts (NL-878, NL-1141).</summary>
    public GraphV2Counts V2 { get; init; } = new(0, 0, 0, 0, 0);
}

/// <summary>The taproot gossip part of the graph (BOLTs PR #1059, NL-878; NL-1141).</summary>
/// <param name="Channels">Channels with a <c>channel_announcement_2</c>, spent ones included.</param>
/// <param name="ChannelsWithBothVersions">Of those, channels also announced with a BOLT 7 <c>channel_announcement</c>.</param>
/// <param name="Policies">Stored <c>channel_update_2</c> directions.</param>
/// <param name="DisabledPolicies">Of those, directions with a disable flag set.</param>
/// <param name="AnnouncedNodes">Nodes with a <c>node_announcement_2</c>.</param>
public sealed record GraphV2Counts(int Channels, int ChannelsWithBothVersions, int Policies, int DisabledPolicies,
                                   int AnnouncedNodes);

/// <summary>The graph ingress's queues.</summary>
/// <param name="QueuedMessages">Messages waiting for a worker.</param>
/// <param name="DroppedMessages">Messages dropped because a queue was full, since the start.</param>
/// <param name="Orphans"><c>channel_update</c>s and <c>node_announcement</c>s waiting for their channel.</param>
/// <param name="PendingAnnouncements">
/// Signed <c>channel_announcement</c>s kept outside the graph until their first <c>channel_update</c> (NL-406).
/// </param>
public sealed record GossipIngressState(int QueuedMessages, long DroppedMessages, int Orphans,
                                        int PendingAnnouncements)
{
    /// <summary>
    /// Keyless <c>channel_announcement_2</c>s kept outside the graph until their first <c>channel_update_2</c>
    /// (NL-1140).
    /// </summary>
    public int PendingAnnouncements2 { get; init; }
}

/// <summary>The gossip sync's state.</summary>
/// <param name="HasCompletedInitialSync">A range sync with at least one peer completed.</param>
/// <param name="Peers">Each connection's sync state.</param>
public sealed record GossipSyncState(bool HasCompletedInitialSync, IReadOnlyList<GossipSyncPeerState> Peers);