namespace NLightning.Domain.Protocol.OnionMessages;

using Channels.ValueObjects;
using Crypto.ValueObjects;

/// <summary>
/// A BOLT 1 <c>sciddir_or_pubkey</c>: a node id (33 bytes, first byte 2 or 3), or a short channel id with a direction
/// (9 bytes, first byte 0 or 1) naming <c>node_id_1</c> (0) or <c>node_id_2</c> (1) of that channel.
/// </summary>
/// <remarks>
/// Shape only: the wire codec is lane M6-A's, and resolving a SCID to a node id needs our channels or the graph.
/// Exactly one of <see cref="NodeId"/> and <see cref="ShortChannelId"/> is set.
/// </remarks>
public sealed record SciddirOrPubkey
{
    /// <summary>
    /// The node id, or null for the SCID form.
    /// </summary>
    public CompactPubKey? NodeId { get; }

    /// <summary>
    /// The short channel id, or null for the node id form.
    /// </summary>
    public ShortChannelId? ShortChannelId { get; }

    /// <summary>
    /// The direction byte of the SCID form: 0 names <c>node_id_1</c>, 1 names <c>node_id_2</c>. 0 for the node id form.
    /// </summary>
    public byte Direction { get; }

    /// <summary>
    /// Whether this is the node id form.
    /// </summary>
    public bool IsNodeId => NodeId is not null;

    private SciddirOrPubkey(CompactPubKey? nodeId, ShortChannelId? shortChannelId, byte direction)
    {
        NodeId = nodeId;
        ShortChannelId = shortChannelId;
        Direction = direction;
    }

    /// <summary>
    /// The node id form.
    /// </summary>
    public static SciddirOrPubkey FromNodeId(CompactPubKey nodeId) => new(nodeId, null, 0);

    /// <summary>
    /// The SCID form.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="direction"/> is not 0 or 1.</exception>
    public static SciddirOrPubkey FromShortChannelId(ShortChannelId shortChannelId, byte direction)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(direction, (byte)1);
        return new SciddirOrPubkey(null, shortChannelId, direction);
    }
}