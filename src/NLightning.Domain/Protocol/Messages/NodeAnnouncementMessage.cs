namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Payloads;

/// <summary>
/// Represents a node_announcement message (BOLT 7, type 257).
/// </summary>
/// <remarks>
/// The payload is parsed (<see cref="NodeAnnouncementPayload"/>); unknown trailing fields are kept so the signature can
/// be verified and the message relayed byte for byte. The message has no TLV extension.
/// </remarks>
public sealed class NodeAnnouncementMessage(NodeAnnouncementPayload payload)
    : BaseMessage(MessageTypes.NodeAnnouncement, payload)
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new NodeAnnouncementPayload Payload => (NodeAnnouncementPayload)base.Payload;
}