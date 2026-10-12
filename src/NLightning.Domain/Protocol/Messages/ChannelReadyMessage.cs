namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Models;
using Payloads;
using Tlv;

/// <summary>
/// Represents a channel_ready message.
/// </summary>
/// <remarks>
/// The channel_ready message indicates that the funding transaction has sufficient confirms for channel use.
/// The message type is 36.
/// </remarks>
public sealed class ChannelReadyMessage : BaseChannelMessage
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new ChannelReadyPayload Payload { get => (ChannelReadyPayload)base.Payload; }

    public ShortChannelIdTlv? ShortChannelIdTlv { get; }

    /// <summary>
    /// Simple taproot channels <c>next_local_nonce</c> (TLV 4): the sender's verification nonce for the next
    /// commitment the peer signs for it. Required on a simple taproot channel, absent otherwise.
    /// </summary>
    public NextLocalNonceTlv? NextLocalNonceTlv { get; }

    public ChannelReadyMessage(ChannelReadyPayload payload, ShortChannelIdTlv? shortChannelIdTlv = null,
                               NextLocalNonceTlv? nextLocalNonceTlv = null,
                               AnnouncementNodeNonceTlv? announcementNodeNonceTlv = null,
                               AnnouncementBitcoinNonceTlv? announcementBitcoinNonceTlv = null)
        : base(MessageTypes.ChannelReady, payload)
    {
        ShortChannelIdTlv = shortChannelIdTlv;
        NextLocalNonceTlv = nextLocalNonceTlv;
        AnnouncementNodeNonceTlv = announcementNodeNonceTlv;
        AnnouncementBitcoinNonceTlv = announcementBitcoinNonceTlv;

        if (ShortChannelIdTlv is not null || NextLocalNonceTlv is not null || AnnouncementNodeNonceTlv is not null
         || AnnouncementBitcoinNonceTlv is not null)
        {
            // BOLT 1: ascending type order (0, 1, 2, 4)
            Extension = new TlvStream();
            Extension.Add(AnnouncementNodeNonceTlv, ShortChannelIdTlv, AnnouncementBitcoinNonceTlv, NextLocalNonceTlv);
        }
    }

    /// <summary>
    /// <c>announcement_node_pubnonce</c> (type 0, taproot gossip, BOLTs PR #1059): with
    /// <see cref="AnnouncementBitcoinNonceTlv"/>, the sender's nonces for the channel's <c>announcement_signatures_2</c>.
    /// </summary>
    public AnnouncementNodeNonceTlv? AnnouncementNodeNonceTlv { get; }

    /// <summary><c>announcement_bitcoin_pubnonce</c> (type 2, BOLTs PR #1059).</summary>
    public AnnouncementBitcoinNonceTlv? AnnouncementBitcoinNonceTlv { get; }
}