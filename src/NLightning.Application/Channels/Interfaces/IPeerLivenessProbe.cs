namespace NLightning.Application.Channels.Interfaces;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Whether a channel's updates may go out now (BOLT2 plan N6-T2 "ping before commit"): the peer is connected on the
/// same connection the channel became usable on (opened, or, with N7, reestablished).
/// </summary>
/// <remarks>
/// <para>
/// BOLT 2: after a reconnection a node MUST wait for the peer's <c>channel_reestablish</c> before sending any other
/// message for the channel, and a message raised for a peer that is not connected is dropped by the peer manager.
/// Every send-side update (<c>IChannelOperations</c>) and every <c>commitment_signed</c> of the
/// <see cref="ICommitScheduler"/> therefore asks this probe first, so an update is never persisted for a link that
/// can't carry it and a <c>commitment_signed</c> never covers an update sent on another connection.
/// </para>
/// <para>
/// The default implementation (<c>ConnectedPeerLivenessProbe</c>) pins the peer's connection when the channel became
/// usable (<see cref="MarkLinkUp"/>, called by <c>ChannelManager</c> when a channel turns Open) and answers true while
/// that same connection is up. A channel loaded at startup is never marked: there is no channel_reestablish until N7,
/// which must call <see cref="MarkLinkUp"/> once the reestablish is done (and then replay the channel's pending
/// events). The BOLT 2 ping before <c>commitment_signed</c> when nothing was received recently is a separate step of the
/// scheduler (<see cref="IPingBeforeCommit"/>, NL-251), outside the channel lock.
/// </para>
/// </remarks>
public interface IPeerLivenessProbe
{
    /// <summary>
    /// True when <paramref name="peerPubKey"/> is connected on the connection <paramref name="channelId"/> was marked
    /// up on.
    /// </summary>
    Task<bool> IsAliveAsync(ChannelId channelId, CompactPubKey peerPubKey,
                            CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that <paramref name="channelId"/> is usable on the peer's current connection (the channel just turned
    /// Open on it; with N7, it was just reestablished on it). Replaces any earlier connection.
    /// </summary>
    void MarkLinkUp(ChannelId channelId, CompactPubKey peerPubKey);
}