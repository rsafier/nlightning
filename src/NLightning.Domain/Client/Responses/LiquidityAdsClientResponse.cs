namespace NLightning.Domain.Client.Responses;

using Crypto.ValueObjects;
using Enums;
using LiquidityAds.Models;

/// <summary>
/// The answer of <c>liquidityads</c> (<c>ClientCommand.LiquidityAds</c>, NL-771): only the part the request's
/// <see cref="Action"/> asks for is filled in.
/// </summary>
public sealed class LiquidityAdsClientResponse
{
    public LiquidityAdsClientResponse(LiquidityAdsAction action)
    {
        Action = action;
    }

    public LiquidityAdsAction Action { get; }

    /// <summary><see cref="LiquidityAdsAction.Rates"/>: the rates we sell at, or null when we do not sell.</summary>
    public WillFundRates? OurRates { get; init; }

    /// <summary>The lease we keep a sold channel open for, in blocks (<c>Node:LiquidityAds:LeaseBlocks</c>).</summary>
    public uint LeaseBlocks { get; init; }

    /// <summary><see cref="LiquidityAdsAction.Rates"/>: the sale negotiations holding wallet inputs now.</summary>
    public int SalesInProgress { get; init; }

    /// <summary><see cref="LiquidityAdsAction.Sellers"/>: the nodes that advertise rates, one per node.</summary>
    public IReadOnlyList<LiquiditySellerInfo> Sellers { get; init; } = [];

    /// <summary><see cref="LiquidityAdsAction.Purchases"/>: the page of purchases, newest first.</summary>
    public IReadOnlyList<LiquidityPurchaseModel> Purchases { get; init; } = [];

    /// <summary>The chain height the lease status is computed at (the node's last processed block).</summary>
    public uint CurrentHeight { get; init; }
}

/// <summary>Where a seller's rates come from.</summary>
public enum LiquiditySellerSource
{
    /// <summary>Its <c>init</c> on the current connection (the freshest).</summary>
    Init = 1,

    /// <summary>Its <c>node_announcement</c> in our gossip graph.</summary>
    NodeAnnouncement = 2
}

/// <summary>A node that sells inbound liquidity (<c>liquidityads sellers</c>).</summary>
/// <param name="NodeId">The seller.</param>
/// <param name="Source">Where the rates were read: its <c>init</c> when it is connected and sent them, else its
/// <c>node_announcement</c>.</param>
/// <param name="Rates">Its rates and payment types.</param>
/// <param name="IsConnected">Whether it is connected to us now.</param>
/// <param name="Alias">Its alias from its <c>node_announcement</c>, or null.</param>
public sealed record LiquiditySellerInfo(
    CompactPubKey NodeId,
    LiquiditySellerSource Source,
    WillFundRates Rates,
    bool IsConnected,
    string? Alias);