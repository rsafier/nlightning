namespace NLightning.Domain.Gossip.Graph;

using Channels.ValueObjects;
using Crypto.ValueObjects;

/// <summary>
/// A public channel of the network graph (from a <c>channel_announcement</c>, BOLT 7 type 256) with the latest
/// policy of each direction. Immutable read model: a change produces a new instance (<see cref="WithPolicy"/>,
/// <see cref="WithSpentAtHeight"/>).
/// </summary>
public sealed record GraphChannel
{
    /// <summary>
    /// Creates a channel.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="nodeId1"/> is not strictly less than
    /// <paramref name="nodeId2"/> (BOLT 7 orders them lexicographically).</exception>
    public GraphChannel(ShortChannelId shortChannelId, CompactPubKey nodeId1, CompactPubKey nodeId2,
                        CompactPubKey bitcoinKey1, CompactPubKey bitcoinKey2, ulong? capacitySat,
                        ReadOnlyMemory<byte> features = default,
                        GraphChannelVerification verification = GraphChannelVerification.Verified)
    {
        if (CompareNodeIds(nodeId1, nodeId2) >= 0)
            throw new ArgumentException("node_id_1 must be lexicographically less than node_id_2.",
                                        nameof(nodeId1));

        ShortChannelId = shortChannelId;
        NodeId1 = nodeId1;
        NodeId2 = nodeId2;
        BitcoinKey1 = bitcoinKey1;
        BitcoinKey2 = bitcoinKey2;
        CapacitySat = capacitySat;
        Features = features.ToArray();
        Verification = verification;
        HasUnknownEvenFeatures = GossipFeatures.HasUnknownEvenBits(features.Span);
    }

    /// <summary>The real short channel id.</summary>
    public ShortChannelId ShortChannelId { get; }

    /// <summary>The lexicographically lesser node id.</summary>
    public CompactPubKey NodeId1 { get; }

    /// <summary>The lexicographically greater node id.</summary>
    public CompactPubKey NodeId2 { get; }

    /// <summary><c>node_id_1</c>'s funding pubkey.</summary>
    public CompactPubKey BitcoinKey1 { get; }

    /// <summary><c>node_id_2</c>'s funding pubkey.</summary>
    public CompactPubKey BitcoinKey2 { get; }

    /// <summary>
    /// The funding output amount in satoshis (from the chain lookup), or null when unknown (unverified channel).
    /// </summary>
    public ulong? CapacitySat { get; }

    /// <summary>The <c>channel_announcement</c> features (big-endian wire bitmap).</summary>
    public ReadOnlyMemory<byte> Features { get; }

    /// <summary>
    /// The features set an even bit we do not know: we MUST NOT route through this channel (B7-CA-03).
    /// </summary>
    public bool HasUnknownEvenFeatures { get; }

    /// <summary>How the funding output was checked.</summary>
    public GraphChannelVerification Verification { get; }

    /// <summary>
    /// The height of the block that spent the funding output, or null while it is unspent. A spent channel is not
    /// routed through and is forgotten 72 blocks later (B7-CA-05, B7-PR-01).
    /// </summary>
    public uint? SpentAtHeight { get; init; }

    /// <summary>The latest policy of direction 0 (announced by <see cref="NodeId1"/>).</summary>
    public GraphPolicy? Policy1 { get; init; }

    /// <summary>The latest policy of direction 1 (announced by <see cref="NodeId2"/>).</summary>
    public GraphPolicy? Policy2 { get; init; }

    /// <summary>
    /// The signed <c>channel_announcement</c> bytes, byte-exact for relay (empty when unknown).
    /// </summary>
    public ReadOnlyMemory<byte> RawAnnouncement { get; init; }

    /// <summary>The capacity in msat, or null when unknown.</summary>
    public ulong? CapacityMsat => CapacitySat is { } sat ? checked(sat * 1_000) : null;

    /// <summary>
    /// The policy of <paramref name="direction"/> (0 or 1), or null when none was received.
    /// </summary>
    public GraphPolicy? GetPolicy(byte direction) => direction switch
    {
        0 => Policy1,
        1 => Policy2,
        _ => throw new ArgumentOutOfRangeException(nameof(direction), "The direction is 0 or 1.")
    };

    /// <summary>
    /// The direction whose origin is <paramref name="nodeId"/> (0 for <see cref="NodeId1"/>, 1 for
    /// <see cref="NodeId2"/>).
    /// </summary>
    /// <exception cref="ArgumentException">The node is not an end of this channel.</exception>
    public byte GetDirectionFrom(CompactPubKey nodeId)
    {
        if (nodeId == NodeId1)
            return 0;
        if (nodeId == NodeId2)
            return 1;

        throw new ArgumentException($"Node {nodeId} is not an end of channel {ShortChannelId}.", nameof(nodeId));
    }

    /// <summary>
    /// The other end of the channel.
    /// </summary>
    /// <exception cref="ArgumentException">The node is not an end of this channel.</exception>
    public CompactPubKey GetOtherNode(CompactPubKey nodeId) => GetDirectionFrom(nodeId) == 0 ? NodeId2 : NodeId1;

    /// <summary>
    /// A copy with <paramref name="policy"/> as the policy of its <see cref="GraphPolicy.Direction"/>.
    /// </summary>
    public GraphChannel WithPolicy(GraphPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.Direction == 0 ? this with { Policy1 = policy } : this with { Policy2 = policy };
    }

    /// <summary>
    /// A copy marked spent at <paramref name="height"/> (null to undo it after a reorg).
    /// </summary>
    public GraphChannel WithSpentAtHeight(uint? height) => this with { SpentAtHeight = height };

    /// <summary>
    /// True when this channel counts as stale at <paramref name="nowUnixSeconds"/> (BOLT 7 "Recommendation on Pruning
    /// Stale Entries", B7-PR-02): it has no policy at all, or the older of its policies is more than
    /// <paramref name="staleAfter"/> old. A missing direction does not make it stale by itself (that direction is
    /// simply unusable).
    /// </summary>
    public bool IsStale(ulong nowUnixSeconds, TimeSpan staleAfter)
    {
        var oldest = (Policy1, Policy2) switch
        {
            (null, null) => (uint?)null,
            ({ } p1, null) => p1.Timestamp,
            (null, { } p2) => p2.Timestamp,
            ({ } p1, { } p2) => Math.Min(p1.Timestamp, p2.Timestamp)
        };

        return oldest is null || (ulong)oldest.Value + (ulong)staleAfter.TotalSeconds < nowUnixSeconds;
    }

    /// <summary>
    /// Compares two compressed node ids byte by byte (the BOLT 7 <c>node_id_1 &lt; node_id_2</c> order).
    /// </summary>
    public static int CompareNodeIds(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) => left.SequenceCompareTo(right);

    public bool Equals(GraphChannel? other) =>
        other is not null
     && ShortChannelId == other.ShortChannelId
     && NodeId1 == other.NodeId1
     && NodeId2 == other.NodeId2
     && BitcoinKey1 == other.BitcoinKey1
     && BitcoinKey2 == other.BitcoinKey2
     && CapacitySat == other.CapacitySat
     && Features.Span.SequenceEqual(other.Features.Span)
     && Verification == other.Verification
     && SpentAtHeight == other.SpentAtHeight
     && Equals(Policy1, other.Policy1)
     && Equals(Policy2, other.Policy2);

    public override int GetHashCode() => HashCode.Combine(ShortChannelId, NodeId1, NodeId2, CapacitySat);
}