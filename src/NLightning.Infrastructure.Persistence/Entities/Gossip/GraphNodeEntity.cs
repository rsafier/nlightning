namespace NLightning.Infrastructure.Persistence.Entities.Gossip;

using Domain.Crypto.ValueObjects;

/// <summary>
/// A BOLT 7 <c>node_announcement</c> we accepted (migration <c>AddGossipGraph</c>, BOLT 7 plan G2-T3). Keyed by the
/// node id; the raw payload is kept because relay and query replies forward it byte-exact.
/// </summary>
public class GraphNodeEntity
{
    public required CompactPubKey NodeId { get; set; }

    /// <summary>The announcement's <c>timestamp</c> (seconds since the epoch).</summary>
    public required uint Timestamp { get; set; }

    public required byte[] Features { get; set; }

    /// <summary>The 32-byte <c>alias</c>.</summary>
    public required byte[] Alias { get; set; }

    /// <summary>The 3-byte <c>rgb_color</c>.</summary>
    public required byte[] Color { get; set; }

    /// <summary>The raw address descriptors.</summary>
    public required byte[] Addresses { get; set; }

    /// <summary>The whole payload (signature included, message type excluded).</summary>
    public required byte[] RawAnnouncement { get; set; }

    /// <summary>When it was received; UTC ticks (<c>UtcTicksConverter</c>).</summary>
    public required DateTimeOffset ReceivedAt { get; set; }

    /// <summary>
    /// 1 = <c>node_announcement</c>, 2 = <c>node_announcement_2</c>, 3 = both (migration <c>AddGossipV2</c>, NL-878).
    /// Without a v1 announcement <see cref="Timestamp"/> is 0 and <see cref="RawAnnouncement"/> empty; the fields are the
    /// v2 announcement's when there is one.
    /// </summary>
    public byte GossipVersions { get; set; } = 1;

    /// <summary>The <c>node_announcement_2</c>'s block height, or null without one.</summary>
    public uint? BlockHeight { get; set; }

    /// <summary>The <c>node_announcement_2</c> payload, or null without one.</summary>
    public byte[]? RawAnnouncement2 { get; set; }

    internal GraphNodeEntity()
    {
    }
}