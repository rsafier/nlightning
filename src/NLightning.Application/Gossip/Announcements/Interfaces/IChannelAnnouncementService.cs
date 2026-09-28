namespace NLightning.Application.Gossip.Announcements.Interfaces;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;

/// <summary>
/// Our side of the BOLT 7 announcement of our public channels (plan §3.2 steps 3-5): when our
/// <c>announcement_signatures</c> may go out, building and signing it, which were sent on the peer's current
/// connection, and assembling the <c>channel_announcement</c> once both halves are in.
/// </summary>
/// <remarks>
/// Every member that takes a <see cref="ChannelModel"/> must be called under that channel's lock. Only
/// <see cref="PrepareOwnAnnouncementSignaturesAsync"/> persists (the sent time, before the message goes out); with
/// <see cref="CreateAnnouncementSignatures"/> the caller marks <see cref="ChannelModel.MarkAnnouncementSignaturesSent"/>
/// and saves the channel itself.
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
    /// Forgets what this process sent and handed on for the channel: a reorg moved its short channel id (NL-350), so
    /// the announcement of the old one is void, and ours for the new one is due again once the new funding block has
    /// the announcement depth, also on the current connection. The caller resets and persists the model's
    /// announcement state (<see cref="ChannelModel.ResetAnnouncementSignatures"/>) under the channel's lock.
    /// </summary>
    void OnShortChannelIdChanged(ChannelId channelId);

    /// <summary>
    /// The channel's <c>channel_announcement</c> with all four signatures, once we sent ours and hold the peer's
    /// (BOLT 7: "has sent AND received a valid <c>announcement_signatures</c>") and the funding transaction has the
    /// announcement depth; null otherwise, or when the stored peer signatures don't verify for the channel's current
    /// short channel id (for example after a reorg moved it). In that last case the peer's half is also forgotten on
    /// the model (the caller persists it, as <see cref="CompleteAnnouncementAsync"/> does), so the channel no longer
    /// counts as announced and ours is sent again on the next connection.
    /// </summary>
    ChannelAnnouncementPayload? TryAssembleAnnouncement(ChannelModel channel);

    /// <summary>
    /// Hands a complete announcement of the channel on: to the graph store and the relay of our own gossip, then our
    /// now public <c>channel_update</c> (to the peer and the relay) and a request for our <c>node_announcement</c>.
    /// Idempotent (per process).
    /// </summary>
    void OnChannelAnnounced(ChannelModel channel, ChannelAnnouncementPayload announcement);

    /// <summary>Whether the channel's announcement was handed on in this process.</summary>
    bool IsAnnouncementComplete(ChannelId channelId);

    /// <summary>
    /// Our <c>announcement_signatures</c> when one is due on <paramref name="peerPubKey"/>'s current connection (BOLT 7
    /// plan G1-T4): the channel may be announced now (<see cref="CanSendAnnouncementSignatures"/>), ours did not go out
    /// on this connection yet, and the halves were not both exchanged before (sent once at the depth, and again on each
    /// reconnection until the peer's half is stored). The sent time is saved through <paramref name="unitOfWork"/>
    /// before the message is returned, and the message is recorded as sent on this connection: the caller must raise
    /// it now, under the channel's lock. Null when nothing is due.
    /// </summary>
    Task<AnnouncementSignaturesMessage?> PrepareOwnAnnouncementSignaturesAsync(ChannelModel channel,
                                                                              CompactPubKey peerPubKey,
                                                                              IUnitOfWork unitOfWork);

    /// <summary>
    /// Assembles and hands on the channel's announcement when both halves are in and it was not handed on in this
    /// process yet (for example after a restart, or when ours went out after the peer's arrived). A stored peer half
    /// that does not sign the current announcement is forgotten and that is saved through <paramref name="unitOfWork"/>.
    /// Call it under the channel's lock.
    /// </summary>
    Task CompleteAnnouncementAsync(ChannelModel channel, IUnitOfWork unitOfWork);

    /// <summary>
    /// Whether we are ready to send <c>announcement_signatures</c> for the funding <paramref name="fundingTxId"/> of the
    /// channel (SP-G-01: a public channel whose splice is locked both ways and has the announcement depth; the original
    /// funding as <see cref="CanSendAnnouncementSignatures"/>). The reestablish retransmits ours when the peer's
    /// <c>my_current_funding_locked</c> sets bit 0 for such a funding (SP-RE-04) and sets bit 0 of its own TLV 5 from
    /// what it holds. Splicing plan SP2-0; implemented by lane SP2-B (SP2-B-T3), read by lane SP2-A.
    /// </summary>
    bool IsReadyForAnnouncementSignatures(ChannelModel channel, TxId fundingTxId) =>
        throw new NotImplementedException("Lane SP2-B (SP2-B-T3, SP-G-01)");

    /// <summary>
    /// Whether the peer's <c>announcement_signatures</c> for <paramref name="shortChannelId"/> names a splice of the
    /// channel for which we have not sent <c>splice_locked</c> yet: it is then kept and handled once we did, not answered
    /// with a warning (BOLT 7 SHOULD, SP-G-01). Splicing plan SP2-0; implemented by lane SP2-B (SP2-B-T3). Also true
    /// for a short channel id a splice lock retired (still in <c>IRetiredScidMap</c>): it matches one of the channel's
    /// fundings, so BOLT 7 wants no warning, and <see cref="DeferRemoteAnnouncementSignatures"/> drops it (NL-490).
    /// </summary>
    bool ShouldDeferRemoteAnnouncementSignatures(ChannelModel channel, ShortChannelId shortChannelId) =>
        throw new NotImplementedException("Lane SP2-B (SP2-B-T3, SP-G-01)");

    /// <summary>
    /// Keeps the peer's <c>announcement_signatures</c> for <paramref name="shortChannelId"/>, a splice of the channel
    /// we have not sent <c>splice_locked</c> for (<see cref="ShouldDeferRemoteAnnouncementSignatures"/>), in memory
    /// until <see cref="ProcessDeferredRemoteAnnouncementSignaturesAsync"/> takes it (BOLT 7: SHOULD defer handling it
    /// until after it has sent <c>splice_locked</c>). A later half for the same channel replaces it. A half for a retired
    /// short channel id is ignored (logged at debug): the replaced funding is never announced again (NL-490).
    /// </summary>
    void DeferRemoteAnnouncementSignatures(ChannelId channelId, ShortChannelId shortChannelId,
                                           ChannelAnnouncementSignatures signatures)
    {
    }

    /// <summary>
    /// Under the channel's lock, once our <c>splice_locked</c> went out and the splice is locked: handles the peer's
    /// deferred half as if it arrived now. It is stored (saved through <paramref name="unitOfWork"/>) when it names the
    /// channel's current short channel id and verifies; ours is returned when it is due on the connection (BOLT 7:
    /// "MUST respond with its own"); the announcement is assembled and handed on with both halves. Null when nothing
    /// was deferred, the half names another short channel id or does not verify, or ours is not due.
    /// </summary>
    Task<AnnouncementSignaturesMessage?> ProcessDeferredRemoteAnnouncementSignaturesAsync(ChannelModel channel,
        CompactPubKey peerPubKey, IUnitOfWork unitOfWork) =>
        Task.FromResult<AnnouncementSignaturesMessage?>(null);
}