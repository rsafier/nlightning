namespace NLightning.Application.Payments.Routing;

using Domain.Crypto.ValueObjects;
using Domain.Gossip.Graph;
using Domain.Routing.Pathfinding;

/// <summary>
/// What <see cref="PaymentRoutePlanner"/> needs to route over the gossip graph in one round (BOLT 7 plan G4-T3):
/// an immutable graph snapshot and a copy of what <see cref="MissionControl"/> learnt. Built by
/// <see cref="GraphPathSource"/>.
/// </summary>
/// <param name="Graph">The graph snapshot.</param>
/// <param name="Liquidity">The liquidity estimates (this round's own copy).</param>
/// <param name="PenalizedNodes">Nodes not to route through (recent NODE failures).</param>
/// <param name="NowUnixSeconds">The current time, for the liquidity decay and the stale check.</param>
public sealed record GraphRoutingContext(
    IGraphView Graph,
    LiquidityEstimates Liquidity,
    IReadOnlySet<CompactPubKey> PenalizedNodes,
    ulong NowUnixSeconds)
{
    /// <summary>Channels whose older policy is older than this are not used (B7-PR-02); null does not check.</summary>
    public TimeSpan? StaleAfter { get; init; }

    /// <summary>The diverse paths to ask the pathfinder for per amount tried (at least 1).</summary>
    public int PathsPerAmount { get; init; } = 3;

    /// <summary>
    /// The random CLTV offset added to the payee's CLTV of every graph route (BOLT 7 shadow route), cut down per route
    /// so it stays within <c>Routing.MaxCltvExpiryDistance</c>.
    /// </summary>
    public uint ShadowCltvOffset { get; init; }

    /// <summary>The pathfinder's cost model; null uses the default.</summary>
    public PathCostModel? CostModel { get; init; }
}