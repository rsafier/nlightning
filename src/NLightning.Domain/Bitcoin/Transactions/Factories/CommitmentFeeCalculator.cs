namespace NLightning.Domain.Bitcoin.Transactions.Factories;

using Channels.Commitments;
using Constants;
using Money;

/// <summary>
/// BOLT 3 "Fee Calculation" and "Trimmed Outputs": the single place that turns a <c>feerate_per_kw</c> into HTLC
/// transaction fees, the commitment base fee and HTLC trimming decisions, for both the commitment transaction factory
/// and the commitment state machine (<see cref="ChannelCommitments"/>, NL-231).
/// </summary>
/// <remarks>
/// All results are whole satoshis, rounded down as the spec requires. With <c>option_anchors</c> both HTLC
/// transactions pay a zero fee (they are fee-bumped by adding inputs), so HTLCs are trimmed against the dust limit
/// alone (NL-195). The <c>…Satoshis</c> members hold the formulas; the <see cref="LightningMoney"/> members and the
/// <see cref="CommitmentSpec"/> members only convert, so the factory and the engine can never disagree on a fee or on
/// <c>num_htlcs</c> (which would make our signature over the peer's commitment invalid).
/// </remarks>
public static class CommitmentFeeCalculator
{
    #region Formulas (satoshis)

    /// <summary>
    /// Fee of an HTLC-timeout transaction in satoshis: 0 with <c>option_anchors</c>, else
    /// <c>feerate_per_kw * 663 / 1000</c>.
    /// </summary>
    public static ulong HtlcTimeoutFeeSatoshis(ulong feeRatePerKw, bool hasAnchors) =>
        hasAnchors ? 0 : checked(feeRatePerKw * WeightConstants.HtlcTimeoutWeightNoAnchors) / 1000;

    /// <summary>
    /// Fee of an HTLC-success transaction in satoshis: 0 with <c>option_anchors</c>, else
    /// <c>feerate_per_kw * 703 / 1000</c>.
    /// </summary>
    public static ulong HtlcSuccessFeeSatoshis(ulong feeRatePerKw, bool hasAnchors) =>
        hasAnchors ? 0 : checked(feeRatePerKw * WeightConstants.HtlcSuccessWeightNoAnchors) / 1000;

    /// <summary>
    /// Fee in satoshis of the second-stage transaction that spends an HTLC output of the commitment holder:
    /// HTLC-timeout for an HTLC the holder offered, HTLC-success for one it received.
    /// </summary>
    public static ulong HtlcTransactionFeeSatoshis(bool isOfferedByHolder, ulong feeRatePerKw, bool hasAnchors) =>
        isOfferedByHolder
            ? HtlcTimeoutFeeSatoshis(feeRatePerKw, hasAnchors)
            : HtlcSuccessFeeSatoshis(feeRatePerKw, hasAnchors);

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
    /// Commitment base fee in satoshis: <c>feerate_per_kw * weight / 1000</c>, rounded down. Trimmed HTLCs are not
    /// counted; their value becomes extra fee, which is not part of the base fee.
    /// </summary>
    public static ulong CommitmentBaseFeeSatoshis(ulong feeRatePerKw, bool hasAnchors, int untrimmedHtlcCount) =>
        checked(feeRatePerKw * CommitmentWeight(hasAnchors, untrimmedHtlcCount)) / 1000;

    /// <summary>
    /// What the funder pays out of its balance before outputs are created, in satoshis: the base fee plus, with
    /// <c>option_anchors</c>, both 330 sat anchors.
    /// </summary>
    public static ulong FunderCostSatoshis(ulong feeRatePerKw, bool hasAnchors, int untrimmedHtlcCount)
    {
        var fee = CommitmentBaseFeeSatoshis(feeRatePerKw, hasAnchors, untrimmedHtlcCount);
        return hasAnchors
                   ? checked(fee + 2 * (ulong)TransactionConstants.AnchorOutputAmount.Satoshi)
                   : fee;
    }

    /// <summary>
    /// BOLT 3 trimming: an HTLC output is omitted when <c>floor(amount_msat / 1000)</c> is below the commitment
    /// holder's <c>dust_limit_satoshis</c> plus its second-stage fee.
    /// </summary>
    /// <param name="amountMsat">The HTLC amount in millisatoshis (rounded down to satoshis).</param>
    /// <param name="isOfferedByHolder">True for an HTLC the commitment holder offered (HTLC-timeout).</param>
    /// <param name="holderDustLimitSatoshis">The dust limit of the commitment holder.</param>
    /// <param name="feeRatePerKw">The commitment feerate.</param>
    /// <param name="hasAnchors">Whether <c>option_anchors</c> applies.</param>
    public static bool IsHtlcTrimmed(ulong amountMsat, bool isOfferedByHolder, ulong holderDustLimitSatoshis,
                                     ulong feeRatePerKw, bool hasAnchors)
    {
        var htlcFee = HtlcTransactionFeeSatoshis(isOfferedByHolder, feeRatePerKw, hasAnchors);
        return amountMsat / 1000 < checked(holderDustLimitSatoshis + htlcFee);
    }

    #endregion

    #region LightningMoney (transaction factories)

    /// <summary>Fee of an HTLC-timeout transaction (see <see cref="HtlcTimeoutFeeSatoshis"/>).</summary>
    public static LightningMoney HtlcTimeoutFee(ulong feeRatePerKw, bool hasAnchors) =>
        LightningMoney.Satoshis(HtlcTimeoutFeeSatoshis(feeRatePerKw, hasAnchors));

    /// <summary>Fee of an HTLC-success transaction (see <see cref="HtlcSuccessFeeSatoshis"/>).</summary>
    public static LightningMoney HtlcSuccessFee(ulong feeRatePerKw, bool hasAnchors) =>
        LightningMoney.Satoshis(HtlcSuccessFeeSatoshis(feeRatePerKw, hasAnchors));

    /// <summary>Fee of an HTLC second-stage transaction (see <see cref="HtlcTransactionFeeSatoshis"/>).</summary>
    public static LightningMoney HtlcTransactionFee(bool isOfferedByHolder, ulong feeRatePerKw, bool hasAnchors) =>
        LightningMoney.Satoshis(HtlcTransactionFeeSatoshis(isOfferedByHolder, feeRatePerKw, hasAnchors));

    /// <summary>Commitment base fee (see <see cref="CommitmentBaseFeeSatoshis"/>).</summary>
    public static LightningMoney CommitmentBaseFee(ulong feeRatePerKw, bool hasAnchors, int untrimmedHtlcCount) =>
        LightningMoney.Satoshis(CommitmentBaseFeeSatoshis(feeRatePerKw, hasAnchors, untrimmedHtlcCount));

    /// <summary>What the funder pays before outputs are created (see <see cref="FunderCostSatoshis"/>).</summary>
    public static LightningMoney FunderCost(ulong feeRatePerKw, bool hasAnchors, int untrimmedHtlcCount) =>
        LightningMoney.Satoshis(FunderCostSatoshis(feeRatePerKw, hasAnchors, untrimmedHtlcCount));

    /// <summary>BOLT 3 trimming (see <see cref="IsHtlcTrimmed(ulong, bool, ulong, ulong, bool)"/>).</summary>
    /// <param name="amount">The HTLC amount (msat precision; rounded down to satoshis).</param>
    /// <param name="isOfferedByHolder">True for an HTLC the commitment holder offered (HTLC-timeout).</param>
    /// <param name="holderDustLimit">The dust limit of the commitment holder (whole satoshis).</param>
    /// <param name="feeRatePerKw">The commitment feerate.</param>
    /// <param name="hasAnchors">Whether <c>option_anchors</c> applies.</param>
    public static bool IsHtlcTrimmed(LightningMoney amount, bool isOfferedByHolder, LightningMoney holderDustLimit,
                                     ulong feeRatePerKw, bool hasAnchors) =>
        IsHtlcTrimmed(amount.MilliSatoshi, isOfferedByHolder, holderDustLimit.MilliSatoshi / 1000, feeRatePerKw,
                      hasAnchors);

    #endregion

    #region CommitmentSpec (commitment state machine)

    /// <summary>
    /// True when <paramref name="htlc"/> produces no output in the commitment held by <paramref name="spec"/>'s
    /// holder (its value goes to fees).
    /// </summary>
    public static bool IsHtlcTrimmed(CommitmentSpec spec, SpecHtlc htlc, ulong holderDustLimitSatoshis,
                                     bool hasAnchors)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return IsHtlcTrimmed(htlc.AmountMsat, htlc.IsOfferedBy(spec.Holder), holderDustLimitSatoshis,
                             spec.FeeratePerKw, hasAnchors);
    }

    /// <summary>
    /// Number of HTLC outputs of the commitment (= <c>num_htlcs</c> of its <c>commitment_signed</c>), trimmed with
    /// the holder's dust limit.
    /// </summary>
    public static int UntrimmedHtlcCount(CommitmentSpec spec, ulong holderDustLimitSatoshis, bool hasAnchors)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec.Htlcs.Count(h => !IsHtlcTrimmed(spec, h, holderDustLimitSatoshis, hasAnchors));
    }

    /// <summary>Sum (msat) of the HTLCs trimmed from the commitment: its dust exposure.</summary>
    public static ulong TrimmedHtlcTotalMsat(CommitmentSpec spec, ulong holderDustLimitSatoshis, bool hasAnchors)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec.Htlcs.Where(h => IsHtlcTrimmed(spec, h, holderDustLimitSatoshis, hasAnchors))
                   .Aggregate(0UL, (sum, h) => checked(sum + h.AmountMsat));
    }

    /// <summary>Base commitment fee (satoshis) of <paramref name="spec"/>, trimmed with the holder's dust limit.</summary>
    public static ulong CommitmentBaseFeeSatoshis(CommitmentSpec spec, ulong holderDustLimitSatoshis,
                                                  bool hasAnchors) =>
        CommitmentBaseFeeSatoshis(spec.FeeratePerKw, hasAnchors,
                                  UntrimmedHtlcCount(spec, holderDustLimitSatoshis, hasAnchors));

    /// <summary>
    /// What the funder pays on top of its HTLCs for this commitment, in msat: the base fee plus both anchors
    /// (<c>2 * 330</c> sat) with <c>option_anchors</c>.
    /// </summary>
    public static ulong FunderCostMsat(CommitmentSpec spec, ulong holderDustLimitSatoshis, bool hasAnchors) =>
        checked(FunderCostSatoshis(spec.FeeratePerKw, hasAnchors,
                                   UntrimmedHtlcCount(spec, holderDustLimitSatoshis, hasAnchors)) * 1_000);

    #endregion
}