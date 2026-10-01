namespace NLightning.Infrastructure.Persistence.Entities.Node;

using Domain.Crypto.ValueObjects;

public class PeerEntity
{
    public required CompactPubKey NodeId { get; set; }
    public required string Host { get; set; }
    public required uint Port { get; set; }
    public required string Type { get; set; }
    public required DateTime LastSeenAt { get; set; }

    /// <summary>
    /// We know no address to dial the peer at (<c>PeerModel.IsInboundOnly</c>, migration <c>AddSpliceHardening</c>,
    /// NL-497): it connected to us from a loopback address. Its channels are still loaded at startup; it is never
    /// dialed.
    /// </summary>
    public bool IsInboundOnly { get; set; }

    /// <summary>
    /// No <c>Channels</c> collection on purpose (NL-134): a channel's peer is identified by its <c>RemoteNodeId</c>
    /// column, but a channel can outlive its <c>Peers</c> row (an inbound peer from a loopback address is never
    /// saved), so a foreign key to <c>Peers</c> cannot be enforced. The EF convention used to invent one on a shadow
    /// <c>PeerEntityNodeId</c> column anyway; nothing navigates from a peer row to its channel rows.
    /// </summary>
    internal PeerEntity() { }
}