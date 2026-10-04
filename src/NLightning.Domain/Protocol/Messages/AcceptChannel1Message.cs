namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Models;
using Payloads;
using Tlv;

/// <summary>
/// Represents an open_channel message.
/// </summary>
/// <remarks>
/// The accept_channel message is sent to the initiator to accept the channel opening.
/// The message type is 33.
/// </remarks>
public sealed class AcceptChannel1Message : BaseChannelMessage
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new AcceptChannel1Payload Payload { get => (AcceptChannel1Payload)base.Payload; }

    /// <summary>
    /// Optional UpfrontShutdownScriptTlv
    /// </summary>
    public UpfrontShutdownScriptTlv? UpfrontShutdownScriptTlv { get; }

    /// <summary>
    /// Optional ChannelTypeTlv
    /// </summary>
    public ChannelTypeTlv? ChannelTypeTlv { get; }

    /// <summary>
    /// Simple taproot channels <c>next_local_nonce</c> (TLV 4): the sender's verification nonce for the first
    /// commitment the peer signs for it. Required on a simple taproot channel, absent otherwise.
    /// </summary>
    public NextLocalNonceTlv? NextLocalNonceTlv { get; }

    public AcceptChannel1Message(AcceptChannel1Payload payload, ChannelTypeTlv? channelTypeTlv,
                                 UpfrontShutdownScriptTlv? upfrontShutdownScriptTlv = null,
                                 NextLocalNonceTlv? nextLocalNonceTlv = null)
        : base(MessageTypes.AcceptChannel, payload)
    {
        UpfrontShutdownScriptTlv = upfrontShutdownScriptTlv;
        ChannelTypeTlv = channelTypeTlv;
        NextLocalNonceTlv = nextLocalNonceTlv;

        Extension = new TlvStream();
        Extension.Add(UpfrontShutdownScriptTlv, ChannelTypeTlv, NextLocalNonceTlv);
    }
}