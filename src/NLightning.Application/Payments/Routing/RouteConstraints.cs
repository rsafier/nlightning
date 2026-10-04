namespace NLightning.Application.Payments.Routing;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Graph;
using Domain.Routing.Pathfinding;

/// <summary>
/// What one payment learnt from its failed attempts (NL-270): nodes and channels to avoid, the policies hops sent in
/// signed <c>channel_update</c>s, liquidity limits and extra CLTV. It lives only as long as the payment.
/// </summary>
/// <remarks>
/// BOLT 4: the origin MAY use the <c>channel_update</c> of an UPDATE failure to retry this payment, and MUST NOT apply
/// it anywhere else (not to a graph, not relayed), so nothing here is shared between payments. Not thread-safe: the
/// payment's session changes and reads it under its payment-hash lock.
/// </remarks>
public sealed class RouteConstraints
{
    /// <summary>
    /// Nodes whose channels are not used (a NODE failure of an intermediate hop).
    /// </summary>
    public HashSet<CompactPubKey> ExcludedNodes { get; } = [];

    /// <summary>
    /// Channels after our first hop (route-hint and graph channels, by short channel id) that are not used.
    /// </summary>
    public HashSet<ShortChannelId> ExcludedChannels { get; } = [];

    /// <summary>
    /// Our own channels that are not used (closed on chain, refused our onion).
    /// </summary>
    public HashSet<ChannelId> ExcludedLocalChannels { get; } = [];

    /// <summary>
    /// A hop's policy from a verified <c>channel_update</c>, by the short channel id of the hint entry it replaces.
    /// </summary>
    public Dictionary<ShortChannelId, HintChannelPolicy> PolicyOverrides { get; } = [];

    /// <summary>
    /// The same verified <c>channel_update</c>s by channel direction, for the graph pathfinder (BOLT 7 plan G4-T3):
    /// they replace the graph's policy of that direction for this payment only, never the graph itself (BOLT 4, D9).
    /// </summary>
    public Dictionary<DirectedChannel, GraphPolicy> GraphPolicyOverrides { get; } = [];

    /// <summary>
    /// An exclusive upper bound on what the payment's HTLCs may forward over a channel after our first hop, together (a
    /// <c>temporary_channel_failure</c> for an HTLC of that size says the channel lacks the liquidity).
    /// </summary>
    public Dictionary<ShortChannelId, ulong> ChannelLiquidityBoundsMsat { get; } = [];

    /// <summary>
    /// An exclusive upper bound on our HTLC on one of our channels (the engine refused an HTLC of that size).
    /// </summary>
    public Dictionary<ChannelId, ulong> LocalLiquidityBoundsMsat { get; } = [];

    /// <summary>
    /// An inclusive lower bound on our HTLC on one of our channels, above the peer's <c>htlc_minimum_msat</c> the
    /// candidate carries (the engine refused an HTLC below the peer's minimum, B2-ADD-S06: the channel's state changed
    /// since the plan; NL-924).
    /// </summary>
    public Dictionary<ChannelId, ulong> LocalHtlcMinimumsMsat { get; } = [];

    /// <summary>
    /// The blinded paths (by their index in the payment's paths) not used again: a failure came from inside them
    /// (BOLT 4: <c>invalid_onion_blinding</c> from the introduction node).
    /// </summary>
    public HashSet<int> ExcludedBlindedPaths { get; } = [];

    /// <summary>
    /// Blocks added to the final <c>outgoing_cltv_value</c> (after an <c>expiry_too_soon</c>: a hop's chain tip was
    /// ahead of ours).
    /// </summary>
    public uint ExtraCltvDelta { get; set; }

    /// <summary>
    /// Lowers the liquidity bound of a hint channel to below <paramref name="failedMsat"/> (never raises it).
    /// </summary>
    public void BoundChannelLiquidity(ShortChannelId shortChannelId, ulong failedMsat)
    {
        if (!ChannelLiquidityBoundsMsat.TryGetValue(shortChannelId, out var current) || failedMsat < current)
            ChannelLiquidityBoundsMsat[shortChannelId] = failedMsat;
    }

    /// <summary>
    /// Lowers the liquidity bound of our channel to below <paramref name="refusedMsat"/> (never raises it).
    /// </summary>
    public void BoundLocalLiquidity(ChannelId channelId, ulong refusedMsat)
    {
        if (!LocalLiquidityBoundsMsat.TryGetValue(channelId, out var current) || refusedMsat < current)
            LocalLiquidityBoundsMsat[channelId] = refusedMsat;
    }

    /// <summary>
    /// Raises the smallest HTLC planned on our channel to above <paramref name="refusedMsat"/> (never lowers it): the
    /// engine refused an HTLC of that size as below the peer's <c>htlc_minimum_msat</c>.
    /// </summary>
    public void RaiseLocalHtlcMinimum(ChannelId channelId, ulong refusedMsat)
    {
        var minimum = refusedMsat == ulong.MaxValue ? refusedMsat : refusedMsat + 1;
        if (!LocalHtlcMinimumsMsat.TryGetValue(channelId, out var current) || minimum > current)
            LocalHtlcMinimumsMsat[channelId] = minimum;
    }
}

/// <summary>
/// A hop's policy for one channel, from the signed <c>channel_update</c> of an UPDATE failure.
/// </summary>
/// <param name="FeeBaseMsat"><c>fee_base_msat</c>.</param>
/// <param name="FeeProportionalMillionths"><c>fee_proportional_millionths</c>.</param>
/// <param name="CltvExpiryDelta"><c>cltv_expiry_delta</c>.</param>
/// <param name="HtlcMinimumMsat"><c>htlc_minimum_msat</c>.</param>
/// <param name="HtlcMaximumMsat"><c>htlc_maximum_msat</c>.</param>
/// <param name="Timestamp">The update's <c>timestamp</c>; a later update must be newer to replace it.</param>
public sealed record HintChannelPolicy(
    uint FeeBaseMsat,
    uint FeeProportionalMillionths,
    ushort CltvExpiryDelta,
    ulong HtlcMinimumMsat,
    ulong HtlcMaximumMsat,
    uint Timestamp);