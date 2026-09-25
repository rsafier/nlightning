namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Payloads;

/// <summary>
/// Represents a channel_update message (BOLT 7, type 258).
/// </summary>
/// <remarks>
/// The payload is parsed (<see cref="ChannelUpdatePayload"/>); unknown trailing fields are kept so the signature can
/// be verified and the message relayed byte for byte. The node-key signature is made and checked through
/// <c>ILightningSigner.SignNodeMessage</c>/<c>VerifyNodeMessage</c> over
/// <see cref="ChannelUpdatePayload.GetSignatureHash"/>.
/// </remarks>
public sealed class ChannelUpdateMessage(ChannelUpdatePayload payload)
    : BaseMessage(MessageTypes.ChannelUpdate, payload)
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new ChannelUpdatePayload Payload => (ChannelUpdatePayload)base.Payload;
}