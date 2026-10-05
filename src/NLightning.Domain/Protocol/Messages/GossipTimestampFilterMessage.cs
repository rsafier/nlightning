namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Gossip.Queries;
using Models;
using Payloads;
using Tlv;

/// <summary>
/// Represents a gossip_timestamp_filter message (BOLT 7, type 265), with the optional taproot gossip
/// <c>block_height_range</c> TLV (type 2, BOLTs PR #1059, NL-878).
/// </summary>
public sealed class GossipTimestampFilterMessage : BaseMessage
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new GossipTimestampFilterPayload Payload => (GossipTimestampFilterPayload)base.Payload;

    /// <summary>The raw <c>block_height_range</c> TLV, if any.</summary>
    public BaseTlv? BlockHeightRangeTlv { get; }

    /// <summary>
    /// The decoded <c>block_height_range</c>, or null when the message carries none or a malformed one.
    /// </summary>
    public GossipBlockHeightRange? BlockHeightRange =>
        BlockHeightRangeTlv is { } tlv && GossipBlockHeightRange.TryDecode(tlv.Value, out var range) ? range : null;

    public GossipTimestampFilterMessage(GossipTimestampFilterPayload payload, BaseTlv? blockHeightRangeTlv = null)
        : base(MessageTypes.GossipTimestampFilter, payload)
    {
        BlockHeightRangeTlv = blockHeightRangeTlv;
        if (blockHeightRangeTlv is not null)
        {
            Extension = new TlvStream();
            Extension.Add(blockHeightRangeTlv);
        }
    }

    /// <summary>A filter with a <c>block_height_range</c> (NL-878).</summary>
    public GossipTimestampFilterMessage(GossipTimestampFilterPayload payload, GossipBlockHeightRange blockHeightRange)
        : this(payload, new BaseTlv(TlvConstants.GossipTimestampFilterBlockHeightRange, blockHeightRange.Encode()))
    {
    }
}