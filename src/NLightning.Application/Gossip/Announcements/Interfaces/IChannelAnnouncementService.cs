namespace NLightning.Application.Gossip.Announcements.Interfaces;

using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;

/// <summary>
/// Our side of the BOLT 7 announcement of our public channels (plan §3.2 steps 3-5): when our
/// <c>announcement_signatures</c> may go out, building and signing it, which were sent on the peer's current
/// connection, and assembling the <c>channel_announcement</c> once both halves are in.
/// </summary>
/// <remarks>
/// Every member that takes a <see cref="ChannelModel"/> must be called under that channel's lock. Nothing here
/// persists: the caller marks <see cref="ChannelModel.MarkAnnouncementSignaturesSent"/> and saves the channel before the
/// message goes out.
/// </remarks>
public interface IChannelAnnouncementService
{
    /// <summary>
    /// Whether our <c>announcement_signatures</c> may be sent now (BOLT 7): the channel is public
    /// (<c>announce_channel</c>), both <c>channel_ready</c> were exchanged (Open), no <c>shutdown</c> was sent or
    /// received, no data loss was detected, and its funding transaction has the announcement depth
    /// (<c>GossipOptions.GetAnnouncementDepth</c>, 6 outside regtest) at our chain tip.
    /// </summary>
    bool CanSendAnnouncementSignatures(ChannelModel channel);

    /// <summary>
    /// Our <c>announcement_signatures</c> for the channel's real short channel id, signed by the signer. Call only when
    /// <see cref="CanSendAnnouncementSignatures"/> is true.
    /// </summary>
    AnnouncementSignaturesMessage CreateAnnouncementSignatures(ChannelModel channel);

    /// <summary>
    /// True when the peer's <paramref name="signatures"/> sign the channel's announcement for
    /// <paramref name="shortChannelId"/> with its node key and its funding key.
    /// </summary>
    bool VerifyRemoteSignatures(ChannelModel channel, ShortChannelId shortChannelId,
                                ChannelAnnouncementSignatures signatures);

    /// <summary>
    /// Whether our <c>announcement_signatures</c> for the channel already went out on the peer's current connection
    /// (so a peer that answers ours with its own does not get ours again: no ping-pong).
    /// </summary>
    bool WasSentOnConnection(ChannelId channelId);

    /// <summary>
    /// Records that our <c>announcement_signatures</c> for the channel went out on <paramref name="peer"/>'s current
    /// connection.
    /// </summary>
    void MarkSentOnConnection(ChannelId channelId, CompactPubKey peer);

    /// <summary>
    /// Forgets what was sent to <paramref name="peer"/>: its connection changed (connected, replaced or dropped), and
    /// BOLT 7 retransmits on every reconnection.
    /// </summary>
    void OnPeerConnectionChanged(CompactPubKey peer);

    /// <summary>
    /// The channel's <c>channel_announcement</c> with all four signatures, once we sent ours and hold the peer's
    /// (BOLT 7: "has sent AND received a valid <c>announcement_signatures</c>") and the funding transaction has the
    /// announcement depth; null otherwise, or when the stored peer signatures don't verify for the channel's current
    /// short channel id (for example after a reorg moved it).
    /// </summary>
    ChannelAnnouncementPayload? TryAssembleAnnouncement(ChannelModel channel);

    /// <summary>
    /// Hands a complete announcement of the channel on (graph store, relay of our own gossip). Idempotent.
    /// </summary>
    void OnChannelAnnounced(ChannelModel channel, ChannelAnnouncementPayload announcement);
}