namespace NLightning.Application.Channels.DualFunding;

using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Models;
using Domain.Money;

/// <summary>
/// A liquidity purchase (liquidity ads, BOLT PR #1153 as Eclair 0.14.3 speaks it, NL-850) negotiated for one attempt of
/// a dual-funded open: the buyer's <c>request_funding</c>, the seller's signed <c>will_fund</c> and the fee. It becomes a
/// <see cref="LiquidityPurchaseModel"/> in the attempt's commitment step.
/// </summary>
/// <param name="Role">Our side of the purchase.</param>
/// <param name="Request">What the buyer asked for.</param>
/// <param name="WillFund">The seller's answer.</param>
/// <param name="Fees">The fee the buyer pays the seller.</param>
/// <param name="ContributedSat">What the seller contributes to the funding output.</param>
internal sealed record DualFundLiquidity(
    LiquidityPurchaseRole Role,
    RequestFunding Request,
    WillFund WillFund,
    LiquidityFees Fees,
    ulong ContributedSat)
{
    /// <summary>The fee from our side, in msat: + when we buy (it leaves our balance), − when we sell.</summary>
    public long LocalFeeMsat => GetLocalFeeMsat(Role, Fees);

    /// <summary>The fee of <paramref name="purchase"/> from our side, in msat (+ bought, − sold).</summary>
    public static long GetLocalFeeMsat(LiquidityPurchaseModel? purchase) =>
        purchase is null ? 0 : GetLocalFeeMsat(purchase.Role, purchase.Fees);

    private static long GetLocalFeeMsat(LiquidityPurchaseRole role, LiquidityFees fees)
    {
        var total = checked((long)fees.TotalMsat);
        return role == LiquidityPurchaseRole.Buyer ? total : -total;
    }

    /// <summary>
    /// The first commitment's balances of shares <paramref name="localShare"/> and <paramref name="remoteShare"/> once
    /// the liquidity fee moved from the buyer to the seller (BOLT PR #1153: the fee is taken from the buyer's next
    /// balance and added to the seller's; no output changes): our balance is our share − <paramref name="localFeeMsat"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The buyer's share cannot pay the fee.</exception>
    public static (LightningMoney Local, LightningMoney Remote) ApplyFee(LightningMoney localShare,
                                                                       LightningMoney remoteShare, long localFeeMsat)
    {
        var local = checked((long)localShare.MilliSatoshi - localFeeMsat);
        var remote = checked((long)remoteShare.MilliSatoshi + localFeeMsat);
        if (local < 0 || remote < 0)
            throw new InvalidOperationException(
                $"The buyer's share cannot pay the liquidity fee of {Math.Abs(localFeeMsat)} msat");

        return (LightningMoney.MilliSatoshis((ulong)local), LightningMoney.MilliSatoshis((ulong)remote));
    }

    /// <summary>
    /// The shares of a funding attempt from its balances: the inverse of <see cref="ApplyFee"/>.
    /// </summary>
    public static (LightningMoney Local, LightningMoney Remote) RemoveFee(LightningMoney localBalance,
                                                                         LightningMoney remoteBalance,
                                                                         long localFeeMsat) =>
        ApplyFee(localBalance, remoteBalance, -localFeeMsat);

    /// <summary>
    /// Why an attempt with these shares cannot carry a liquidity fee of <paramref name="localFeeMsat"/> (from our
    /// side), or null: the buyer's share must pay the fee, and the opener's balance after it must still pay the first
    /// commitment's fee (and anchors), <paramref name="openerCommitmentCost"/> (BOLT 2: the opener pays it).
    /// </summary>
    public static string? GetBalanceViolation(LightningMoney localShare, LightningMoney remoteShare,
                                              long localFeeMsat, bool weAreOpener, LightningMoney openerCommitmentCost)
    {
        if (localFeeMsat == 0)
            return null;

        var local = (long)localShare.MilliSatoshi - localFeeMsat;
        var remote = (long)remoteShare.MilliSatoshi + localFeeMsat;
        if (local < 0 || remote < 0)
            return $"the buyer's contribution cannot pay the liquidity fee of {Math.Abs(localFeeMsat)} msat";

        var opener = weAreOpener ? local : remote;
        return opener < (long)openerCommitmentCost.MilliSatoshi
                   ? $"the opener's balance of {opener} msat after the liquidity fee cannot pay the first commitment's "
                   + $"fee {openerCommitmentCost}"
                   : null;
    }
}

/// <summary>
/// Our own <c>request_funding</c> while we wait for the seller's answer (<c>accept_channel2</c> or <c>tx_ack_rbf</c>).
/// </summary>
/// <param name="Request">What we asked for.</param>
/// <param name="MaxFeeSat">Our fee limit (null = <c>Node:LiquidityAds:MaxFeeSat</c>).</param>
/// <param name="FundingFeeratePerKw">The attempt's feerate (the mining fee part of the fee).</param>
internal sealed record DualFundLiquidityRequest(RequestFunding Request, ulong? MaxFeeSat, uint FundingFeeratePerKw);