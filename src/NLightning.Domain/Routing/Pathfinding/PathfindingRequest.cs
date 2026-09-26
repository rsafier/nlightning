namespace NLightning.Domain.Routing.Pathfinding;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Gossip.Graph;

/// <summary>
/// A pathfinding query for <see cref="GraphPathfinder"/>. CLTV values are deltas relative to the current block
/// height; amounts are msat.
/// </summary>
/// <param name="Source">Our node id (pays no fee to itself and adds no CLTV delta).</param>
/// <param name="Target">The payee.</param>
/// <param name="AmountMsat">What the payee must receive.</param>
/// <param name="FinalCltvDelta">The payee's <c>cltv_expiry</c> minus the current height: the invoice's
/// <c>min_final_cltv_expiry_delta</c> plus any safety margin the caller adds.</param>
public sealed record PathfindingRequest(CompactPubKey Source, CompactPubKey Target, ulong AmountMsat,
                                        uint FinalCltvDelta)
{
    /// <summary>The BOLT 4 hop limit (a 1300-byte onion fits 20 legacy-size payloads).</summary>
    public const int DefaultMaxHops = 20;

    /// <summary>
    /// The largest total CLTV delta (our first HTLC's <c>cltv_expiry</c> minus the height), shadow offset included.
    /// Default 2016 (<c>Routing.MaxCltvExpiryDistance</c>).
    /// </summary>
    public uint MaxTotalCltvDelta { get; init; } = 2016;

    /// <summary>The most channels a path may have. Default 20.</summary>
    public int MaxHops { get; init; } = DefaultMaxHops;

    /// <summary>The most the path may cost in fees, or null for no limit.</summary>
    public ulong? MaxFeeMsat { get; init; }

    /// <summary>Nodes not to route through (the payee and we are never excluded by this set).</summary>
    public IReadOnlySet<CompactPubKey>? ExcludedNodes { get; init; }

    /// <summary>Channels not to use in either direction.</summary>
    public IReadOnlySet<ShortChannelId>? ExcludedChannels { get; init; }

    /// <summary>Channel directions not to use.</summary>
    public IReadOnlySet<DirectedChannel>? ExcludedEdges { get; init; }

    /// <summary>
    /// Policies that replace the graph's for this query (e.g. the <c>channel_update</c> of a BOLT 4 failure, which
    /// must never be written to the graph, D9).
    /// </summary>
    public IReadOnlyDictionary<DirectedChannel, GraphPolicy>? PolicyOverrides { get; init; }

    /// <summary>
    /// Edges not in the graph: our private channels and BOLT 11 route hint entries.
    /// </summary>
    public IReadOnlyList<ExtraEdge>? ExtraEdges { get; init; }

    /// <summary>
    /// The live state of our own channels, by short channel id (real or alias). When set, an edge from
    /// <see cref="Source"/> is used only if its channel is listed and usable and can carry the amount; its
    /// <c>disable</c> bit is ignored (the live state wins) and its probability is 1. When null, our edges are
    /// treated like any other.
    /// </summary>
    public IReadOnlyDictionary<ShortChannelId, LocalChannelState>? LocalChannels { get; init; }

    /// <summary>What mission control learned; null uses the prior for every edge.</summary>
    public LiquidityEstimates? Liquidity { get; init; }

    /// <summary>
    /// The current UNIX time: for the liquidity decay and, when <see cref="StaleAfter"/> is set, the stale-channel
    /// check (B7-PR-02).
    /// </summary>
    public ulong NowUnixSeconds { get; init; }

    /// <summary>Skip channels stale at <see cref="NowUnixSeconds"/>; null does not check.</summary>
    public TimeSpan? StaleAfter { get; init; }

    /// <summary>
    /// Pay a payee whose <c>node_announcement</c> sets unknown even features (BOLT 7 allows it only when the invoice
    /// does not set those bits; the caller decides). Default false.
    /// </summary>
    public bool AllowTargetUnknownFeatures { get; init; }

    /// <summary>
    /// A random CLTV offset added to the payee's CLTV (the BOLT 7 shadow route; see
    /// <see cref="ShadowCltv.ComputeOffset"/>), cut down so the path stays within <see cref="MaxTotalCltvDelta"/>.
    /// </summary>
    public uint ShadowCltvOffset { get; init; }

    /// <summary>The cost model; null uses <see cref="PathCostModel.Default"/>.</summary>
    public PathCostModel? CostModel { get; init; }
}