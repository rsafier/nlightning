using System.Text;

namespace NLightning.Domain.Gossip.Graph;

using Addresses;
using Crypto.ValueObjects;
using Protocol.Payloads;

/// <summary>
/// A node of the network graph, from its latest accepted <c>node_announcement</c> (BOLT 7 type 257) and/or
/// <c>node_announcement_2</c> (taproot gossip type 269, NL-878). Immutable read model. A node that is an end of a
/// graph channel but never announced itself has no <see cref="GraphNode"/>.
/// </summary>
/// <remarks>
/// A node announced with both protocols keeps both raw announcements (<see cref="RawAnnouncement"/>,
/// <see cref="RawAnnouncement2"/>) and both orderings (<see cref="Timestamp"/>, <see cref="BlockHeight"/>); its
/// features, alias, color and addresses are the <c>node_announcement_2</c>'s (the draft favours the new protocol).
/// </remarks>
public sealed record GraphNode
{
    /// <summary>
    /// The length of the <c>alias</c> field.
    /// </summary>
    public const int AliasLength = 32;

    /// <summary>
    /// The length of the <c>rgb_color</c> field.
    /// </summary>
    public const int ColorLength = 3;

    /// <summary>
    /// Creates a node.
    /// </summary>
    /// <exception cref="ArgumentException">The alias is not 32 bytes or the color not 3 bytes.</exception>
    public GraphNode(CompactPubKey nodeId, uint timestamp, ReadOnlyMemory<byte> features, ReadOnlySpan<byte> alias,
                     ReadOnlySpan<byte> rgbColor, IReadOnlyList<AddressDescriptor>? addresses = null)
    {
        if (alias.Length != AliasLength)
            throw new ArgumentException($"The alias is {AliasLength} bytes.", nameof(alias));
        if (rgbColor.Length != ColorLength)
            throw new ArgumentException($"The color is {ColorLength} bytes.", nameof(rgbColor));

        NodeId = nodeId;
        Timestamp = timestamp;
        Features = features.ToArray();
        Alias = alias.ToArray();
        RgbColor = rgbColor.ToArray();
        Addresses = addresses?.ToArray() ?? [];
        HasUnknownEvenFeatures = GossipFeatures.HasUnknownEvenBits(features.Span);
    }

    /// <summary>The node id.</summary>
    public CompactPubKey NodeId { get; }

    /// <summary>The <c>node_announcement</c>'s timestamp (0 for a node announced with v2 only).</summary>
    public uint Timestamp { get; }

    /// <summary>
    /// The protocols the node announced itself with (<see cref="GraphGossipVersions.V1"/> by default).
    /// </summary>
    public GraphGossipVersions Versions { get; init; } = GraphGossipVersions.V1;

    /// <summary>True when the node has a BOLT 7 <c>node_announcement</c>.</summary>
    public bool HasV1 => (Versions & GraphGossipVersions.V1) != 0;

    /// <summary>True when the node has a <c>node_announcement_2</c>.</summary>
    public bool HasV2 => (Versions & GraphGossipVersions.V2) != 0;

    /// <summary>The <c>node_announcement_2</c>'s block height, or null without one.</summary>
    public uint? BlockHeight { get; init; }

    /// <summary>The signed <c>node_announcement_2</c> payload, byte-exact for relay (empty without one).</summary>
    public ReadOnlyMemory<byte> RawAnnouncement2 { get; init; }

    /// <summary>The node features (big-endian wire bitmap).</summary>
    public ReadOnlyMemory<byte> Features { get; }

    /// <summary>
    /// The features set an even bit we do not know: we MUST NOT route through this node, and must not pay it unless
    /// the invoice's features allow it (B7-NA-04).
    /// </summary>
    public bool HasUnknownEvenFeatures { get; }

    /// <summary>The raw 32-byte alias (UTF-8, zero padded by a compliant origin).</summary>
    public ReadOnlyMemory<byte> Alias { get; }

    /// <summary>The 3-byte color (red, green, blue).</summary>
    public ReadOnlyMemory<byte> RgbColor { get; }

    /// <summary>The usable address descriptors (already filtered by the BOLT 7 receiver rules).</summary>
    public IReadOnlyList<AddressDescriptor> Addresses { get; }

    /// <summary>
    /// The signed <c>node_announcement</c> bytes, byte-exact for relay (empty when unknown).
    /// </summary>
    public ReadOnlyMemory<byte> RawAnnouncement { get; init; }

    /// <summary>
    /// The alias as text: the bytes up to the first zero, decoded as UTF-8 with invalid sequences replaced. The alias
    /// is self-chosen by the node: sanitize it before displaying it (BOLT 7 "Security Considerations for Node
    /// Aliases").
    /// </summary>
    public string AliasText
    {
        get
        {
            var span = Alias.Span;
            var end = span.IndexOf((byte)0);
            return Encoding.UTF8.GetString(end < 0 ? span : span[..end]);
        }
    }

    /// <summary>The color as <c>#rrggbb</c>.</summary>
    public string ColorHex => "#" + Convert.ToHexStringLower(RgbColor.Span);

    /// <summary>
    /// The node of a <c>node_announcement_2</c> (NL-878): its alias zero padded to 32 bytes, its color (black when
    /// absent), its usable addresses (port 0 left out) and its raw bytes; <see cref="Timestamp"/> 0, as for a node
    /// without a <c>node_announcement</c>.
    /// </summary>
    public static GraphNode FromNodeAnnouncement2(NodeAnnouncement2Payload announcement, ReadOnlyMemory<byte> raw)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        var alias = new byte[AliasLength];
        if (announcement.Alias is { } announcedAlias)
            announcedAlias.Span.CopyTo(alias);

        var color = announcement.Color is { } announcedColor ? announcedColor.ToArray() : new byte[ColorLength];
        return new GraphNode(announcement.NodeId, 0, announcement.Features, alias, color,
                             announcement.Addresses.ToList())
        {
            Versions = GraphGossipVersions.V2,
            BlockHeight = announcement.BlockHeight,
            RawAnnouncement2 = raw.ToArray()
        };
    }

    public bool Equals(GraphNode? other) =>
        other is not null
     && NodeId == other.NodeId
     && Timestamp == other.Timestamp
     && Features.Span.SequenceEqual(other.Features.Span)
     && Alias.Span.SequenceEqual(other.Alias.Span)
     && RgbColor.Span.SequenceEqual(other.RgbColor.Span)
     && Versions == other.Versions
     && BlockHeight == other.BlockHeight
     && Addresses.SequenceEqual(other.Addresses);

    public override int GetHashCode() => HashCode.Combine(NodeId, Timestamp);
}