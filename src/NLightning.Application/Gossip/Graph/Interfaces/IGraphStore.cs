using System.Diagnostics.CodeAnalysis;

namespace NLightning.Application.Gossip.Graph.Interfaces;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Graph;

/// <summary>
/// The node's network graph (plan BOLT7 §3.1, D2): authoritative in memory, persisted write-behind through
/// <c>IGraphDbRepository</c> and loaded at startup. Every change goes through one writer lock (never a channel lock);
/// readers get immutable values and snapshots.
/// </summary>
public interface IGraphStore
{
    /// <summary>True once <see cref="LoadAsync"/> completed.</summary>
    bool IsLoaded { get; }

    /// <summary>The number of channels (spent ones included).</summary>
    int ChannelCount { get; }

    /// <summary>The number of announced nodes.</summary>
    int NodeCount { get; }

    /// <summary>The number of stored <c>channel_update</c> directions.</summary>
    int PolicyCount { get; }

    /// <summary>Changes not written to the database yet.</summary>
    int PendingChanges { get; }

    /// <summary>
    /// Raised (outside the writer lock) after a channel was added, so what waited for it (orphan updates, node
    /// announcements) can be replayed.
    /// </summary>
    event EventHandler<GraphChannel>? ChannelAdded;

    /// <summary>
    /// Loads the persisted graph (once; later calls return at once). Changes made before the load are kept and win.
    /// </summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the pending changes, in batches of their own unit of work (G5-T3). On failure the batch that failed and
    /// the ones after it stay pending; the batches before it are written.
    /// </summary>
    Task FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>An immutable snapshot for pathfinding and listings (rebuilt only after a change).</summary>
    IGraphView GetSnapshot();

    /// <summary>
    /// The counts and the estimated memory of the graph (G5-T1 budget, G5-T4 <c>describegraph</c>); O(1), kept up to
    /// date on every change.
    /// </summary>
    GraphMemoryEstimate GetMemoryEstimate();

    /// <summary>The channel with <paramref name="shortChannelId"/>.</summary>
    bool TryGetChannel(ShortChannelId shortChannelId, [NotNullWhen(true)] out GraphChannel? channel);

    /// <summary>The announcement of <paramref name="nodeId"/>.</summary>
    bool TryGetNode(CompactPubKey nodeId, [NotNullWhen(true)] out GraphNode? node);

    /// <summary>True when <paramref name="nodeId"/> is an end of at least one graph channel.</summary>
    bool NodeHasChannels(CompactPubKey nodeId);

    /// <summary>The funding transaction id of a channel, when known (from the chain check or our own channel).</summary>
    bool TryGetFundingTxId(ShortChannelId shortChannelId, out TxId fundingTxId);

    /// <summary>
    /// Records the funding transaction id of a stored channel (the pruner's startup lookup of a row saved without one)
    /// and marks the channel for the next flush when it changed (NL-352); false when the channel is unknown.
    /// </summary>
    bool TrySetFundingTxId(ShortChannelId shortChannelId, TxId fundingTxId);

    /// <summary>
    /// The channel whose funding output is <paramref name="transactionId"/>:<paramref name="outputIndex"/>, when its
    /// funding transaction id is known (plan D4: the pruner's spent check, O(1) per block input).
    /// </summary>
    bool TryGetChannelByFundingOutpoint(TxId transactionId, uint outputIndex, out ShortChannelId shortChannelId);

    /// <summary>The channels whose funding transaction id is not known (after a restart, or unverified).</summary>
    IReadOnlyList<ShortChannelId> GetChannelsWithoutFundingTxId();

    /// <summary>When a channel's announcement was stored (the stale baseline of a channel without updates).</summary>
    bool TryGetChannelReceivedAt(ShortChannelId shortChannelId, out DateTimeOffset receivedAt);

    /// <summary>True when the gossip of <paramref name="nodeId"/> is ignored (a ban that has not ended).</summary>
    bool IsBanned(CompactPubKey nodeId);

    /// <summary>
    /// Adds a channel (with <see cref="GraphChannel.RawAnnouncement"/> set); false when one with that short channel id
    /// is already stored.
    /// </summary>
    bool TryAddChannel(GraphChannel channel, TxId? fundingTxId = null);

    /// <summary>
    /// Sets the policy of one direction of a stored channel; false when the channel is unknown or the stored policy of
    /// that direction is not older.
    /// </summary>
    bool TryApplyPolicy(ShortChannelId shortChannelId, GraphPolicy policy);

    /// <summary>
    /// Stores a node announcement (with <see cref="GraphNode.RawAnnouncement"/> set); false when the stored one is not
    /// older.
    /// </summary>
    bool TryApplyNode(GraphNode node);

    /// <summary>
    /// Stores our own node announcement in memory only: its row is written by the node announcement service before it
    /// is published (plan G1-T6), so the write-behind never writes it (an older pending write of it is dropped, so it
    /// can never overwrite the newer row); false when the stored one is not older.
    /// </summary>
    bool TryApplyOwnNode(GraphNode node);

    /// <summary>Ignores the gossip of <paramref name="nodeId"/> until <paramref name="until"/>.</summary>
    void Ban(CompactPubKey nodeId, string reason, DateTimeOffset until);

    /// <summary>Marks the funding output of a channel spent at <paramref name="height"/>; false when unknown.</summary>
    bool MarkSpent(ShortChannelId shortChannelId, uint height);

    /// <summary>Reorg: clears the spend of every channel spent above <paramref name="height"/>; returns how many.</summary>
    int ClearSpentAbove(uint height);

    /// <summary>Removes a channel and its policies; false when unknown.</summary>
    bool RemoveChannel(ShortChannelId shortChannelId);

    /// <summary>Removes a node announcement; false when unknown.</summary>
    bool RemoveNode(CompactPubKey nodeId);
}