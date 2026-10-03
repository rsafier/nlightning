namespace NLightning.Domain.Client.Requests;

using Enums;
using LiquidityAds.Enums;

/// <summary>
/// <c>liquidityads rates|sellers|purchases</c> (<c>ClientCommand.LiquidityAds</c>, NL-850).
/// </summary>
public sealed class LiquidityAdsClientRequest
{
    public LiquidityAdsClientRequest(LiquidityAdsAction action)
    {
        Action = action;
    }

    public LiquidityAdsAction Action { get; }

    /// <summary><see cref="LiquidityAdsAction.Purchases"/>: only this side (bought or sold), or both when null.</summary>
    public LiquidityPurchaseRole? Role { get; init; }

    /// <summary><see cref="LiquidityAdsAction.Purchases"/>: only this status, or every status when null.</summary>
    public LiquidityPurchaseStatus? Status { get; init; }

    /// <summary><see cref="LiquidityAdsAction.Purchases"/>: how many of the newest to skip.</summary>
    public int Skip { get; init; }

    /// <summary><see cref="LiquidityAdsAction.Purchases"/>: how many to return at most.</summary>
    public int Take { get; init; } = 25;
}