namespace NLightning.Domain.Client.Enums;

/// <summary>
/// What <c>liquidityads</c> shows (<c>ClientCommand.LiquidityAds</c>, NL-850). The values go on the wire: never
/// renumber them.
/// </summary>
public enum LiquidityAdsAction
{
    /// <summary><c>liquidityads rates</c>: the rates we sell at, or none (we do not sell).</summary>
    Rates = 1,

    /// <summary><c>liquidityads sellers</c>: the nodes that advertise rates, from their <c>init</c> and their
    /// <c>node_announcement</c>.</summary>
    Sellers = 2,

    /// <summary><c>liquidityads purchases</c>: the liquidity we bought and sold, newest first, with the lease status.</summary>
    Purchases = 3
}