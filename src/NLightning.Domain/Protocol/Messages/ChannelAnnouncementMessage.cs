namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Payloads;

/// <summary>
/// Represents a channel_announcement message (BOLT 7, type 256).
/// </summary>
/// <remarks>
/// The payload is parsed (<see cref="ChannelAnnouncementPayload"/>); unknown trailing fields are kept so the four
/// signatures can be verified and the message relayed byte for byte. The message has no TLV extension.
/// </remarks>
public sealed class ChannelAnnouncementMessage(ChannelAnnouncementPayload payload)
    : BaseMessage(MessageTypes.ChannelAnnouncement, payload)
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new ChannelAnnouncementPayload Payload => (ChannelAnnouncementPayload)base.Payload;
}