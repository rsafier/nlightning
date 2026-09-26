namespace NLightning.Domain.Gossip.Persistence;

using Crypto.ValueObjects;

/// <summary>
/// A stored <c>node_announcement</c> (BOLT 7, table <c>GraphNodes</c>, primary key the node id): its parsed fields and
/// the raw signed bytes, which relay and query replies forward byte-exact.
/// </summary>
/// <remarks>The byte arrays are held as given (not copied), and record equality compares them by reference.</remarks>
public sealed record GraphNodeRecord
{
    /// <summary>The length of <see cref="Alias"/>.</summary>
    public const int AliasLength = 32;

    /// <summary>The length of <see cref="Color"/> (<c>rgb_color</c>).</summary>
    public const int ColorLength = 3;

    public CompactPubKey NodeId { get; }

    /// <summary>The announcement's <c>timestamp</c> (seconds since the epoch).</summary>
    public uint Timestamp { get; }

    /// <summary>The raw <c>features</c> bytes (big-endian wire order).</summary>
    public byte[] Features { get; }

    /// <summary>The 32-byte <c>alias</c> as sent (UTF-8, zero padded).</summary>
    public byte[] Alias { get; }

    /// <summary>The 3-byte <c>rgb_color</c>.</summary>
    public byte[] Color { get; }

    /// <summary>The raw <c>addresses</c> bytes (the address descriptors, without the length prefix).</summary>
    public byte[] Addresses { get; }

    /// <summary>
    /// The whole <c>node_announcement</c> payload as received (signature included, the 2-byte message type excluded).
    /// </summary>
    public byte[] RawAnnouncement { get; }

    /// <summary>When we received (or created) this announcement.</summary>
    public DateTimeOffset ReceivedAt { get; }

    /// <exception cref="ArgumentException">The alias is not 32 bytes or the color not 3 bytes.</exception>
    public GraphNodeRecord(CompactPubKey nodeId, uint timestamp, byte[] features, byte[] alias, byte[] color,
                           byte[] addresses, byte[] rawAnnouncement, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(alias);
        ArgumentNullException.ThrowIfNull(color);
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(rawAnnouncement);
        if (alias.Length != AliasLength)
            throw new ArgumentException($"The alias must be {AliasLength} bytes", nameof(alias));
        if (color.Length != ColorLength)
            throw new ArgumentException($"The color must be {ColorLength} bytes", nameof(color));

        NodeId = nodeId;
        Timestamp = timestamp;
        Features = features;
        Alias = alias;
        Color = color;
        Addresses = addresses;
        RawAnnouncement = rawAnnouncement;
        ReceivedAt = receivedAt;
    }
}