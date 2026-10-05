namespace NLightning.Domain.Gossip.Graph;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Protocol.GossipV2;

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
                        CompactPubKey? bitcoinKey1, CompactPubKey? bitcoinKey2, ulong? capacitySat,
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

    /// <summary>
    /// <c>node_id_1</c>'s funding pubkey (null only for a <c>channel_announcement_2</c> that proves a P2TR output with
    /// the 3-key aggregate, without bitcoin keys).
    /// </summary>
    public CompactPubKey? BitcoinKey1 { get; }

    /// <summary><c>node_id_2</c>'s funding pubkey (null like <see cref="BitcoinKey1"/>).</summary>
    public CompactPubKey? BitcoinKey2 { get; }

    /// <summary>
    /// The protocols the channel was announced with: <see cref="GraphGossipVersions.V1"/> (default) when
    /// <see cref="RawAnnouncement"/> holds a <c>channel_announcement</c>, <see cref="GraphGossipVersions.V2"/> when
    /// <see cref="RawAnnouncement2"/> holds a <c>channel_announcement_2</c>.
    /// </summary>
    public GraphGossipVersions Versions { get; init; } = GraphGossipVersions.V1;

    /// <summary>True when the channel has a BOLT 7 <c>channel_announcement</c>.</summary>
    public bool HasV1 => (Versions & GraphGossipVersions.V1) != 0;

    /// <summary>True when the channel has a <c>channel_announcement_2</c>.</summary>
    public bool HasV2 => (Versions & GraphGossipVersions.V2) != 0;

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

    /// <summary>The latest <c>channel_update</c> of direction 0 (announced by <see cref="NodeId1"/>).</summary>
    public GraphPolicy? Policy1 { get; init; }

    /// <summary>The latest <c>channel_update</c> of direction 1 (announced by <see cref="NodeId2"/>).</summary>
    public GraphPolicy? Policy2 { get; init; }

    /// <summary>The latest <c>channel_update_2</c> of direction 0 (NL-878; its timestamp is a block height).</summary>
    public GraphPolicy? Policy1V2 { get; init; }

    /// <summary>The latest <c>channel_update_2</c> of direction 1.</summary>
    public GraphPolicy? Policy2V2 { get; init; }

    /// <summary>
    /// The signed <c>channel_announcement</c> bytes, byte-exact for relay (empty when unknown or v2 only).
    /// </summary>
    public ReadOnlyMemory<byte> RawAnnouncement { get; init; }

    /// <summary>
    /// The signed <c>channel_announcement_2</c> payload, byte-exact for relay and query replies (empty without one).
    /// </summary>
    public ReadOnlyMemory<byte> RawAnnouncement2 { get; init; }

    /// <summary>The capacity in msat, or null when unknown.</summary>
    public ulong? CapacityMsat => CapacitySat is { } sat ? checked(sat * 1_000) : null;

    /// <summary>
    /// The capacity for the routing estimates: <see cref="CapacityMsat"/> when the chain gave it, otherwise the larger
    /// <c>htlc_maximum_msat</c> of the two policies (LND sets it to the capacity less the reserve, CLN to at most the
    /// capacity, so it is a lower bound close to the real one), or null without a policy. Only a hint for the
    /// liquidity model: an <see cref="GraphChannelVerification.Assumed"/> or
    /// <see cref="GraphChannelVerification.Unverified"/> channel has no capacity from the chain.
    /// </summary>
    public ulong? EstimatedCapacityMsat =>
        CapacityMsat ?? (GetRoutingPolicy(0)?.HtlcMaximumMsat, GetRoutingPolicy(1)?.HtlcMaximumMsat) switch
        {
            (null, null) => null,
            ({ } max1, null) => max1,
            (null, { } max2) => max2,
            ({ } max1, { } max2) => Math.Max(max1, max2)
        };

    /// <summary>
    /// True when the funding output was checked against the chain (<see cref="GraphChannelVerification.Verified"/>)
    /// or the channel is ours: only such a channel is relayed and served in query replies.
    /// </summary>
    public bool IsChainChecked =>
        Verification is GraphChannelVerification.Verified or GraphChannelVerification.Own;

    /// <summary>
    /// The BOLT 7 <c>channel_update</c> policy of <paramref name="direction"/> (0 or 1), or null when none was received.
    /// </summary>
    public GraphPolicy? GetPolicy(byte direction) => direction switch
    {
        0 => Policy1,
        1 => Policy2,
        _ => throw new ArgumentOutOfRangeException(nameof(direction), "The direction is 0 or 1.")
    };

    /// <summary>
    /// The policy of <paramref name="direction"/> from the updates of gossip <paramref name="version"/> (1 or 2).
    /// </summary>
    public GraphPolicy? GetPolicy(byte direction, byte version) => version switch
    {
        1 => GetPolicy(direction),
        2 => direction switch
        {
            0 => Policy1V2,
            1 => Policy2V2,
            _ => throw new ArgumentOutOfRangeException(nameof(direction), "The direction is 0 or 1.")
        },
        _ => throw new ArgumentOutOfRangeException(nameof(version), "The gossip version is 1 or 2.")
    };

    /// <summary>
    /// The policy routing uses for <paramref name="direction"/>: the <c>channel_update_2</c> when there is one (the
    /// draft: "favour the new protocol when making routing decisions"), else the <c>channel_update</c>.
    /// </summary>
    public GraphPolicy? GetRoutingPolicy(byte direction) => GetPolicy(direction, 2) ?? GetPolicy(direction);

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
    /// A copy with <paramref name="policy"/> as the policy of its <see cref="GraphPolicy.Direction"/> and
    /// <see cref="GraphPolicy.GossipVersion"/>.
    /// </summary>
    public GraphChannel WithPolicy(GraphPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.IsV2)
            return policy.Direction == 0 ? this with { Policy1V2 = policy } : this with { Policy2V2 = policy };

        return policy.Direction == 0 ? this with { Policy1 = policy } : this with { Policy2 = policy };
    }

    /// <summary>
    /// A copy marked spent at <paramref name="height"/> (null to undo it after a reorg).
    /// </summary>
    public GraphChannel WithSpentAtHeight(uint? height) => this with { SpentAtHeight = height };

    /// <summary>
    /// True when this channel counts as stale at <paramref name="nowUnixSeconds"/> (BOLT 7 "Recommendation on Pruning
    /// Stale Entries", B7-PR-02): it has no routing policy at all (<see cref="GetRoutingPolicy"/>), or one of them is
    /// stale. A missing direction does not make it stale by itself (that direction is simply unusable). A BOLT 7
    /// policy is stale when more than <paramref name="staleAfter"/> old; a <c>channel_update_2</c> (block height) when
    /// it is below <paramref name="tipHeight"/> − <c>max_backdate_blocks</c> (2016, the draft's window), and never
    /// when the tip is unknown.
    /// </summary>
    public bool IsStale(ulong nowUnixSeconds, TimeSpan staleAfter, uint? tipHeight = null)
    {
        var policy1 = GetRoutingPolicy(0);
        var policy2 = GetRoutingPolicy(1);
        if (policy1 is null && policy2 is null)
            return true;

        return IsPolicyStale(policy1) || IsPolicyStale(policy2);

        bool IsPolicyStale(GraphPolicy? policy)
        {
            if (policy is null)
                return false;
            if (policy.IsV2)
                return IsBlockHeightStale(policy.Timestamp, tipHeight);

            return (ulong)policy.Timestamp + (ulong)staleAfter.TotalSeconds < nowUnixSeconds;
        }
    }

    /// <summary>
    /// True when a <c>channel_update_2</c>/<c>node_announcement_2</c> dated <paramref name="blockHeight"/> is older
    /// than <paramref name="tipHeight"/> − <c>max_backdate_blocks</c> (the draft's receiver rule and staleness window);
    /// false when the tip is unknown.
    /// </summary>
    public static bool IsBlockHeightStale(uint blockHeight, uint? tipHeight) =>
        tipHeight is { } tip && (ulong)blockHeight + GossipV2Constants.MaxBackdateBlocks < tip;

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
     && Versions == other.Versions
     && Equals(Policy1, other.Policy1)
     && Equals(Policy2, other.Policy2)
     && Equals(Policy1V2, other.Policy1V2)
     && Equals(Policy2V2, other.Policy2V2);

    public override int GetHashCode() => HashCode.Combine(ShortChannelId, NodeId1, NodeId2, CapacitySat);
}