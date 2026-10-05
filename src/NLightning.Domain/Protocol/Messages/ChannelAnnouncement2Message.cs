namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Payloads;

/// <summary>
/// Represents a channel_announcement_2 message (taproot gossip, BOLTs PR #1059, type 267).
/// </summary>
/// <remarks>
/// A pure TLV message: the payload keeps every record as received (<see cref="ChannelAnnouncement2Payload.Stream"/>),
/// so the MuSig2 signature can be verified and the message relayed byte for byte.
/// </remarks>
public sealed class ChannelAnnouncement2Message(ChannelAnnouncement2Payload payload)
    : BaseMessage(MessageTypes.ChannelAnnouncement2, payload)
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new ChannelAnnouncement2Payload Payload => (ChannelAnnouncement2Payload)base.Payload;
}