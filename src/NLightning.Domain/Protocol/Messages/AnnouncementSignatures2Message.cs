namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Payloads;

/// <summary>
/// Represents an announcement_signatures_2 message (taproot gossip, BOLTs PR #1059, type 260).
/// </summary>
/// <remarks>
/// A channel message (it carries a <c>channel_id</c> and is exchanged only with the channel peer), handled under the
/// channel's lock like v1's <c>announcement_signatures</c>.
/// </remarks>
public sealed class AnnouncementSignatures2Message(AnnouncementSignatures2Payload payload)
    : BaseChannelMessage(MessageTypes.AnnouncementSignatures2, payload)
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new AnnouncementSignatures2Payload Payload => (AnnouncementSignatures2Payload)base.Payload;
}