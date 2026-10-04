namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Models;
using Payloads;
using Tlv;

/// <summary>
/// Represents an open_channel2 message.
/// </summary>
/// <remarks>
/// The open_channel message is sent to another peer in order to start the channel negotiation.
/// The message type is 32.
/// </remarks>
public sealed class OpenChannel1Message : BaseChannelMessage
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new OpenChannel1Payload Payload { get => (OpenChannel1Payload)base.Payload; }

    public UpfrontShutdownScriptTlv? UpfrontShutdownScriptTlv { get; }
    public ChannelTypeTlv? ChannelTypeTlv { get; }

    /// <summary>
    /// Simple taproot channels <c>next_local_nonce</c> (TLV 4): the sender's verification nonce for the first
    /// commitment the peer signs for it. Required on a simple taproot channel, absent otherwise.
    /// </summary>
    public NextLocalNonceTlv? NextLocalNonceTlv { get; }

    public OpenChannel1Message(OpenChannel1Payload payload, ChannelTypeTlv? channelTypeTlv,
                               UpfrontShutdownScriptTlv? upfrontShutdownScriptTlv = null,
                               NextLocalNonceTlv? nextLocalNonceTlv = null)
        : base(MessageTypes.OpenChannel, payload)
    {
        UpfrontShutdownScriptTlv = upfrontShutdownScriptTlv;
        ChannelTypeTlv = channelTypeTlv;
        NextLocalNonceTlv = nextLocalNonceTlv;

        Extension = new TlvStream();
        Extension.Add(UpfrontShutdownScriptTlv, ChannelTypeTlv, NextLocalNonceTlv);
    }
}