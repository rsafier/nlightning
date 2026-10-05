namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Models;
using Payloads;
using Tlv;

/// <summary>
/// Represents a splice_locked message (BOLT 2 "Channel Splicing", type 77, SP-LK-01).
/// </summary>
/// <remarks>
/// Sent when a splice transaction reaches acceptable depth; the splice completes when both sides sent it for the same
/// txid (SP-LK-03). No TLVs. Handled in wave SP2 (lane SP2-B); the wire is lane SP1-A's.
/// </remarks>
/// <param name="payload">The splice_locked payload.</param>
public sealed class SpliceLockedMessage : BaseChannelMessage
{
    public SpliceLockedMessage(SpliceLockedPayload payload,
                               AnnouncementNodeNonceTlv? announcementNodeNonceTlv = null,
                               AnnouncementBitcoinNonceTlv? announcementBitcoinNonceTlv = null)
        : base(MessageTypes.SpliceLocked, payload)
    {
        AnnouncementNodeNonceTlv = announcementNodeNonceTlv;
        AnnouncementBitcoinNonceTlv = announcementBitcoinNonceTlv;
        if (AnnouncementNodeNonceTlv is null && AnnouncementBitcoinNonceTlv is null)
            return;

        // BOLT 1: ascending type order (0, 2)
        Extension = new TlvStream();
        Extension.Add(AnnouncementNodeNonceTlv, AnnouncementBitcoinNonceTlv);
    }

    /// <summary>
    /// <c>announcement_node_pubnonce</c> (type 0, taproot gossip, BOLTs PR #1059): the sender's nonces for the
    /// re-announcement of the spliced channel, bound to the splice txid.
    /// </summary>
    public AnnouncementNodeNonceTlv? AnnouncementNodeNonceTlv { get; }

    /// <summary><c>announcement_bitcoin_pubnonce</c> (type 2, BOLTs PR #1059).</summary>
    public AnnouncementBitcoinNonceTlv? AnnouncementBitcoinNonceTlv { get; }

    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new SpliceLockedPayload Payload { get => (SpliceLockedPayload)base.Payload; }
}