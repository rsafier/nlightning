using System.Text;

namespace NLightning.Domain.Gossip.Graph;

using Addresses;
using Crypto.ValueObjects;

/// <summary>
/// A node of the network graph, from its latest accepted <c>node_announcement</c> (BOLT 7 type 257). Immutable read
/// model. A node that is an end of a graph channel but never announced itself has no <see cref="GraphNode"/>.
/// </summary>
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

    /// <summary>The announcement's timestamp.</summary>
    public uint Timestamp { get; }

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

    public bool Equals(GraphNode? other) =>
        other is not null
     && NodeId == other.NodeId
     && Timestamp == other.Timestamp
     && Features.Span.SequenceEqual(other.Features.Span)
     && Alias.Span.SequenceEqual(other.Alias.Span)
     && RgbColor.Span.SequenceEqual(other.RgbColor.Span)
     && Addresses.SequenceEqual(other.Addresses);

    public override int GetHashCode() => HashCode.Combine(NodeId, Timestamp);
}