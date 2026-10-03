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
                               NextLocalNonceTlv? nextLocalNonceTlv = null)
        : base(MessageTypes.ChannelReady, payload)
    {
        ShortChannelIdTlv = shortChannelIdTlv;
        NextLocalNonceTlv = nextLocalNonceTlv;

        if (ShortChannelIdTlv is not null || NextLocalNonceTlv is not null)
        {
            Extension = new TlvStream();
            Extension.Add(ShortChannelIdTlv, NextLocalNonceTlv);
        }
    }
}