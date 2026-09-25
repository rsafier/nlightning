namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Payloads;

/// <summary>
/// Represents a node_announcement message (BOLT 7, type 257). The payload is kept as raw bytes.
/// </summary>
public sealed class NodeAnnouncementMessage(GossipPayload payload) : GossipMessage(MessageTypes.NodeAnnouncement, payload);