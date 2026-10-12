namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Payloads;

/// <summary>
/// Represents a node_announcement_2 message (taproot gossip, BOLTs PR #1059, type 269).
/// </summary>
/// <remarks>
/// A pure TLV message signed with the node key (BIP 340) over <see cref="NodeAnnouncement2Payload.GetSignatureHash"/>;
/// its timestamp is a block height.
/// </remarks>
public sealed class NodeAnnouncement2Message(NodeAnnouncement2Payload payload)
    : BaseMessage(MessageTypes.NodeAnnouncement2, payload)
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new NodeAnnouncement2Payload Payload => (NodeAnnouncement2Payload)base.Payload;
}