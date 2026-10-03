using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.LiquidityAds.Enums;

/// <summary>
/// Request for LiquidityAds (ClientCommand 46, NL-771): <c>liquidityads rates|sellers|purchases</c>.
/// </summary>
[MessagePackObject]
public sealed class LiquidityAdsIpcRequest
{
    [Key(0)] public required LiquidityAdsAction Action { get; init; }

    /// <summary><see cref="LiquidityAdsAction.Purchases"/>: only this side, or both when null.</summary>
    [Key(1)] public LiquidityPurchaseRole? Role { get; init; }

    /// <summary><see cref="LiquidityAdsAction.Purchases"/>: only this status, or every status when null.</summary>
    [Key(2)] public LiquidityPurchaseStatus? Status { get; init; }

    /// <summary><see cref="LiquidityAdsAction.Purchases"/>: how many of the newest to skip.</summary>
    [Key(3)] public int Skip { get; init; }

    /// <summary><see cref="LiquidityAdsAction.Purchases"/>: how many to return at most.</summary>
    [Key(4)] public int Take { get; set; } = 25;

    public LiquidityAdsClientRequest ToClientRequest() =>
        new(Action) { Role = Role, Status = Status, Skip = Skip, Take = Take };
}