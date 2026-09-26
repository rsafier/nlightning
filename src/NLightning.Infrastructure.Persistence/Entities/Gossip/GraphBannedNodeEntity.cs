namespace NLightning.Infrastructure.Persistence.Entities.Gossip;

using Domain.Crypto.ValueObjects;

/// <summary>
/// A node whose gossip is ignored until <see cref="Until"/> (migration <c>AddGossipGraph</c>, BOLT 7 plan §3.8).
/// </summary>
public class GraphBannedNodeEntity
{
    public required CompactPubKey NodeId { get; set; }
    public required string Reason { get; set; }

    /// <summary>When the ban ends; UTC ticks (<c>UtcTicksConverter</c>).</summary>
    public required DateTimeOffset Until { get; set; }

    internal GraphBannedNodeEntity()
    {
    }
}