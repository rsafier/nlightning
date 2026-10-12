namespace NLightning.Application.Channels.Splicing;

using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Models;
using LiquidityAds;

/// <summary>
/// The liquidity purchase (liquidity ads, BOLT PR #1153 as Eclair 0.14.3 speaks it, NL-850) one splice attempt carries:
/// the buyer's <c>request_funding</c> (in <c>splice_init</c> or <c>tx_init_rbf</c>), the seller's signed
/// <c>will_fund</c> (in <c>splice_ack</c> or <c>tx_ack_rbf</c>) and the fee, which moves from the buyer's balance to the
/// seller's on the new funding (<see cref="FeeMsat"/>, through the <c>ChannelFunding</c> balance deltas).
/// </summary>
/// <remarks>Memory only, on the attempt's <see cref="SpliceNegotiation"/>; the purchase row
/// (<c>LiquidityPurchases</c>) is what is remembered, saved with our splice <c>commitment_signed</c>.</remarks>
internal sealed class SpliceLiquidity
{
    public SpliceLiquidity(LiquidityPurchaseRole role, RequestFunding request, ulong? maxFeeSat = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        Role = role;
        Request = request;
        MaxFeeSat = maxFeeSat;
    }

    /// <summary>Whether we buy (we sent the request) or sell (we answer it).</summary>
    public LiquidityPurchaseRole Role { get; }

    /// <summary>The buyer's request.</summary>
    public RequestFunding Request { get; }

    /// <summary>The buyer's fee limit (null = <c>Node:LiquidityAds:MaxFeeSat</c>); unused by the seller.</summary>
    public ulong? MaxFeeSat { get; }

    /// <summary>The seller's signed answer, once made (seller) or received and checked (buyer).</summary>
    public WillFund? WillFund { get; set; }

    /// <summary>The fee, once known.</summary>
    public LiquidityFees? Fees { get; set; }

    /// <summary>The seller's griefing-cap slot (D-L5), released when the negotiation ends.</summary>
    public LiquidityAdsService.LiquiditySale? Sale { get; set; }

    /// <summary>The purchase record staged with our splice <c>commitment_signed</c>.</summary>
    public LiquidityPurchaseModel? Purchase { get; set; }

    /// <summary>The fee from our point of view: + when we pay it (we buy), − when we earn it (we sell), 0 before it is
    /// known.</summary>
    public long FeeMsat => Fees is { } fees
                               ? Role == LiquidityPurchaseRole.Buyer ? checked((long)fees.TotalMsat)
                                 : -checked((long)fees.TotalMsat)
                               : 0;

    /// <summary>Releases the seller's slot (idempotent).</summary>
    public void EndSale()
    {
        Sale?.Dispose();
        Sale = null;
    }

    /// <summary>The fee of a stored purchase from our point of view (+ we paid, − we earned).</summary>
    public static long GetFeeMsat(LiquidityPurchaseModel? purchase) =>
        purchase is null ? 0
        : purchase.Role == LiquidityPurchaseRole.Buyer ? checked((long)purchase.TotalFeeMsat)
        : -checked((long)purchase.TotalFeeMsat);

    /// <summary>The attempt of a stored purchase, as it was negotiated (after a restart, or for an RBF that repeats it).
    /// </summary>
    public static SpliceLiquidity FromPurchase(LiquidityPurchaseModel purchase)
    {
        ArgumentNullException.ThrowIfNull(purchase);
        return new SpliceLiquidity(purchase.Role,
                                   new RequestFunding(purchase.RequestedSat, purchase.Rate,
                                                      LiquidityPaymentDetails.FromChannelBalance), purchase.MaxFeeSat)
        {
            WillFund = new WillFund(purchase.Rate, purchase.FundingScript, purchase.Signature),
            Fees = purchase.Fees,
            Purchase = purchase
        };
    }
}