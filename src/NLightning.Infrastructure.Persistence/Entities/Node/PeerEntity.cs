namespace NLightning.Infrastructure.Persistence.Entities.Node;

using Channel;
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

    public virtual ICollection<ChannelEntity>? Channels { get; set; }

    internal PeerEntity() { }
}