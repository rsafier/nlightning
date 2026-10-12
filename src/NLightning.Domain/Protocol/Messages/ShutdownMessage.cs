namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Models;
using Payloads;
using Tlv;

/// <summary>
/// Represents a shutdown message.
/// </summary>
/// <remarks>
/// The shutdown message is sent by either node to initiate closing, along with the scriptpubkey it wants to be paid to.
/// The message type is 38.
/// </remarks>
public sealed class ShutdownMessage : BaseChannelMessage
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new ShutdownPayload Payload { get => (ShutdownPayload)base.Payload; }

    /// <summary>
    /// Simple taproot channels <c>shutdown_nonce</c> (TLV 8): the sender's closee nonce. Required on a simple taproot
    /// channel, absent otherwise.
    /// </summary>
    public ShutdownNonceTlv? ShutdownNonceTlv { get; }

    public ShutdownMessage(ShutdownPayload payload, ShutdownNonceTlv? shutdownNonceTlv = null)
        : base(MessageTypes.Shutdown, payload)
    {
        ShutdownNonceTlv = shutdownNonceTlv;

        if (ShutdownNonceTlv is not null)
        {
            Extension = new TlvStream();
            Extension.Add(ShutdownNonceTlv);
        }
    }
}