namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Models;
using Payloads;
using Tlv;

/// <summary>
/// Represents a revoke_and_ack message.
/// </summary>
/// <remarks>
/// The revoke_and_ack message is used as a reply to the commitment_signed message.
/// The message type is 133.
/// </remarks>
public sealed class RevokeAndAckMessage : BaseChannelMessage
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new RevokeAndAckPayload Payload { get => (RevokeAndAckPayload)base.Payload; }

    /// <summary>
    /// Simple taproot channels <c>next_local_nonces</c> (TLV 22): the sender's verification nonce for its next
    /// commitment, one per active funding. Required on a simple taproot channel, absent otherwise.
    /// </summary>
    public NextLocalNoncesTlv? NextLocalNoncesTlv { get; }

    public RevokeAndAckMessage(RevokeAndAckPayload payload, NextLocalNoncesTlv? nextLocalNoncesTlv = null)
        : base(MessageTypes.RevokeAndAck, payload)
    {
        NextLocalNoncesTlv = nextLocalNoncesTlv;

        if (NextLocalNoncesTlv is not null)
        {
            Extension = new TlvStream();
            Extension.Add(NextLocalNoncesTlv);
        }
    }
}