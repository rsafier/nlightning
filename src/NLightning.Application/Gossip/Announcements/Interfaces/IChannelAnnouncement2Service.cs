namespace NLightning.Application.Gossip.Announcements.Interfaces;

using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;

/// <summary>
/// The <c>channel_announcement_2</c> of our public simple taproot channels (taproot gossip, BOLTs PR #1059, NL-878).
/// Every method that takes a channel runs under that channel's lock and returns the messages to send to its peer.
/// </summary>
public interface IChannelAnnouncement2Service
{
    /// <summary>A public simple taproot channel, with <c>option_gossip_v2</c> advertised by us.</summary>
    bool IsV2Channel(ChannelModel channel);

    /// <summary>Whether the channel's <c>channel_announcement_2</c> is complete in this process.</summary>
    bool IsAnnounced(ChannelId channelId);

    /// <summary>The channel's unsigned <c>channel_announcement_2</c> (BIP 86 funding: both keys, no merkle root).</summary>
    ChannelAnnouncement2Payload BuildUnsigned(ChannelModel channel);

    /// <summary>
    /// The block-driven step: at the announcement depth our nonces (a re-sent <c>channel_ready</c>) when this
    /// connection did not carry them yet, and our <c>announcement_signatures_2</c> once both nonce pairs are known.
    /// </summary>
    IReadOnlyList<IChannelMessage> Advance(ChannelModel channel, CompactPubKey peer);

    /// <summary>The peer's announcement nonces from its <c>channel_ready</c> (TLVs 0 and 2).</summary>
    IReadOnlyList<IChannelMessage> OnChannelReadyNonces(ChannelModel channel, CompactPubKey peer,
                                                        AnnouncementNodeNonceTlv? nodeNonce,
                                                        AnnouncementBitcoinNonceTlv? bitcoinNonce);

    /// <summary>
    /// The TLVs of our <c>channel_reestablish</c>: <c>my_current_funding_locked</c> (retransmit bit 1 while the
    /// announcement is not complete in this process) and fresh <c>announcement_nonces</c> (a new session).
    /// </summary>
    (MyCurrentFundingLockedTlv? FundingLocked, AnnouncementNoncesTlv? Nonces) CreateReestablishTlvs(
        ChannelModel channel, CompactPubKey peer);

    /// <summary>The peer's <c>channel_reestablish</c> announcement TLVs, once ours went out on this connection.</summary>
    IReadOnlyList<IChannelMessage> OnReestablish(ChannelModel channel, CompactPubKey peer,
                                                 ChannelReestablishMessage message);

    /// <summary>The peer's <c>announcement_signatures_2</c>: verified, stored, answered with ours when due.</summary>
    IReadOnlyList<IChannelMessage> OnAnnouncementSignatures2(ChannelModel channel, CompactPubKey peer,
                                                             AnnouncementSignatures2Payload payload);

    /// <summary>The peer's connection changed: its sessions (and our live nonces) are dropped.</summary>
    void OnPeerConnectionChanged(CompactPubKey peer);

    /// <summary>The channel's funding moved (a splice lock, a reorg) or it closes: its session and announcement go.</summary>
    void OnFundingChanged(ChannelId channelId);
}