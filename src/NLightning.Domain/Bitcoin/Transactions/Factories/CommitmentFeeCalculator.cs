namespace NLightning.Domain.Bitcoin.Transactions.Factories;

using Constants;
using Money;

/// <summary>
/// BOLT 3 "Fee Calculation" and "Trimmed Outputs": the single place that turns a <c>feerate_per_kw</c> into HTLC
/// transaction fees, the commitment base fee and HTLC trimming decisions.
/// </summary>
/// <remarks>
/// All results are whole satoshis, rounded down as the spec requires. With <c>option_anchors</c> both HTLC
/// transactions pay a zero fee (they are fee-bumped by adding inputs), so HTLCs are trimmed against the dust limit
/// alone (NL-195).
/// </remarks>
public static class CommitmentFeeCalculator
{
    /// <summary>
    /// Fee of an HTLC-timeout transaction: 0 with <c>option_anchors</c>, else <c>feerate_per_kw * 663 / 1000</c>.
    /// </summary>
    public static LightningMoney HtlcTimeoutFee(ulong feeRatePerKw, bool hasAnchors) =>
        hasAnchors
            ? LightningMoney.Zero
            : LightningMoney.Satoshis(checked(feeRatePerKw * WeightConstants.HtlcTimeoutWeightNoAnchors) / 1000);

    /// <summary>
    /// Fee of an HTLC-success transaction: 0 with <c>option_anchors</c>, else <c>feerate_per_kw * 703 / 1000</c>.
    /// </summary>
    public static LightningMoney HtlcSuccessFee(ulong feeRatePerKw, bool hasAnchors) =>
        hasAnchors
            ? LightningMoney.Zero
            : LightningMoney.Satoshis(checked(feeRatePerKw * WeightConstants.HtlcSuccessWeightNoAnchors) / 1000);

    /// <summary>
    /// Fee of the second-stage transaction that spends an HTLC output of the commitment holder: HTLC-timeout for an
    /// HTLC the holder offered, HTLC-success for one it received.
    /// </summary>
    public static LightningMoney HtlcTransactionFee(bool isOfferedByHolder, ulong feeRatePerKw, bool hasAnchors) =>
        isOfferedByHolder
            ? HtlcTimeoutFee(feeRatePerKw, hasAnchors)
            : HtlcSuccessFee(feeRatePerKw, hasAnchors);

    /// <summary>
    /// Expected commitment weight: 724 (1124 with <c>option_anchors</c>) + 172 per untrimmed HTLC output.
    /// </summary>
    public static ulong CommitmentWeight(bool hasAnchors, int untrimmedHtlcCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(untrimmedHtlcCount);

        var baseWeight = hasAnchors
                             ? WeightConstants.CommitmentWeightAnchors
                             : WeightConstants.CommitmentWeightNoAnchors;
        return checked((ulong)baseWeight + (ulong)untrimmedHtlcCount * WeightConstants.HtlcOutputWeight);
    }

    /// <summary>
    /// Commitment base fee: <c>feerate_per_kw * weight / 1000</c>, rounded down. Trimmed HTLCs are not counted; their
    /// value becomes extra fee, which is not part of the base fee.
    /// </summary>
    public static LightningMoney CommitmentBaseFee(ulong feeRatePerKw, bool hasAnchors, int untrimmedHtlcCount) =>
        LightningMoney.Satoshis(checked(feeRatePerKw * CommitmentWeight(hasAnchors, untrimmedHtlcCount)) / 1000);

    /// <summary>
    /// What the funder pays out of its balance before outputs are created: the base fee plus, with
    /// <c>option_anchors</c>, both 330 sat anchors.
    /// </summary>
    public static LightningMoney FunderCost(ulong feeRatePerKw, bool hasAnchors, int untrimmedHtlcCount)
    {
        var fee = CommitmentBaseFee(feeRatePerKw, hasAnchors, untrimmedHtlcCount);
        return hasAnchors
                   ? fee + TransactionConstants.AnchorOutputAmount + TransactionConstants.AnchorOutputAmount
                   : fee;
    }

    /// <summary>
    /// BOLT 3 trimming: an HTLC output is omitted when <c>floor(amount_msat / 1000)</c> minus its second-stage fee
    /// would be below the commitment holder's <c>dust_limit_satoshis</c>.
    /// </summary>
    /// <param name="amount">The HTLC amount (msat precision; rounded down to satoshis).</param>
    /// <param name="isOfferedByHolder">True for an HTLC the commitment holder offered (HTLC-timeout).</param>
    /// <param name="holderDustLimit">The dust limit of the commitment holder.</param>
    /// <param name="feeRatePerKw">The commitment feerate.</param>
    /// <param name="hasAnchors">Whether <c>option_anchors</c> applies.</param>
    public static bool IsHtlcTrimmed(LightningMoney amount, bool isOfferedByHolder, LightningMoney holderDustLimit,
                                     ulong feeRatePerKw, bool hasAnchors)
    {
        var htlcFee = HtlcTransactionFee(isOfferedByHolder, feeRatePerKw, hasAnchors);
        return (ulong)amount.Satoshi < checked((ulong)holderDustLimit.Satoshi + (ulong)htlcFee.Satoshi);
    }
}