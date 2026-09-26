namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Payloads;

/// <summary>
/// Represents an announcement_signatures message (BOLT 7, type 259).
/// </summary>
/// <remarks>
/// A channel message (it carries a <c>channel_id</c> and is exchanged only with the channel peer), so the peer service
/// raises it with the other channel messages and <c>ChannelManager</c> handles it under the channel's lock. Bytes after
/// the known fields (a TLV extension, none defined) are kept in <see cref="AnnouncementSignaturesPayload.ExtraData"/>.
/// </remarks>
public sealed class AnnouncementSignaturesMessage(AnnouncementSignaturesPayload payload)
    : BaseChannelMessage(MessageTypes.AnnouncementSignatures, payload)
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new AnnouncementSignaturesPayload Payload => (AnnouncementSignaturesPayload)base.Payload;
}