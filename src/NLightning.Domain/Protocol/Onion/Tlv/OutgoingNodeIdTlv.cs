namespace NLightning.Domain.Protocol.Onion.Tlv;

using Constants;
using Crypto.ValueObjects;
using Protocol.Tlv;

/// <summary>
/// Onion hop payload TLV 14 (<c>outgoing_node_id</c>): the next trampoline node, named in a trampoline onion payload
/// instead of a <c>short_channel_id</c> (BOLTs PR 836).
/// </summary>
/// <remarks>
/// The value is only checked for length and a 0x02/0x03 prefix; it is NOT validated as a point on the curve.
/// </remarks>
public class OutgoingNodeIdTlv : BaseTlv
{
    /// <summary>
    /// The next trampoline node's id.
    /// </summary>
    public CompactPubKey OutgoingNodeId { get; }

    public OutgoingNodeIdTlv(CompactPubKey outgoingNodeId) : base(OnionPayloadTlvTypes.OutgoingNodeId)
    {
        OutgoingNodeId = outgoingNodeId;

        Value = outgoingNodeId;
        Length = Value.Length;
    }
}