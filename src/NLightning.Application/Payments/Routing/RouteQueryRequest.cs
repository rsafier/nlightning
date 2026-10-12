namespace NLightning.Application.Payments.Routing;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Models;
using Domain.Money;

/// <summary>
/// A route query with LND <c>QueryRoutes</c>' restrictions (NL-1242), answered by
/// <see cref="Interfaces.IRouteQueryService.QueryRouteAsync"/> exactly as a payment's first round with one part would
/// plan it, nothing sent.
/// </summary>
/// <param name="Payee">The destination; this node for a circular route (a rebalance).</param>
/// <param name="Amount">What the destination must receive.</param>
public sealed record RouteQueryRequest(CompactPubKey Payee, LightningMoney Amount)
{
    /// <summary>The most the route may cost in fees.</summary>
    public required LightningMoney MaxFee { get; init; }

    /// <summary>
    /// The destination's CLTV delta: its HTLC expires exactly at the height plus this many blocks (LND adds no
    /// padding in <c>QueryRoutes</c>, unlike a payment).
    /// </summary>
    public required ushort FinalCltvDelta { get; init; }

    /// <summary>The most blocks our HTLC's <c>cltv_expiry</c> may lie above the height (LND's <c>cltv_limit</c>); null
    /// uses the node's <c>Routing.MaxCltvExpiryDistance</c>.</summary>
    public uint? MaxTotalCltvDelta { get; init; }

    /// <summary>Nodes the route must not pass through.</summary>
    public IReadOnlySet<CompactPubKey> IgnoredNodes { get; init; } = new HashSet<CompactPubKey>();

    /// <summary>Directed edges (from one node to the other) the route must not use, in either of its channels.</summary>
    public IReadOnlyList<(CompactPubKey From, CompactPubKey To)> IgnoredPairs { get; init; } = [];

    /// <summary>The short channel ids of our channels the first hop may use; null allows every one.</summary>
    public IReadOnlySet<ShortChannelId>? OutgoingChannels { get; init; }

    /// <summary>The node the route must reach the destination from (its last forwarding hop); null allows any.</summary>
    public CompactPubKey? LastHop { get; init; }

    /// <summary>Route hints to a private destination (BOLT 11 <c>r</c> entries).</summary>
    public IReadOnlyList<IReadOnlyList<RoutingInfo>> RouteHints { get; init; } = [];

    /// <summary>Whether mission control's learnt liquidity and node penalties apply; false plans on the graph's
    /// a-priori estimates alone.</summary>
    public bool UseMissionControl { get; init; } = true;
}