namespace NLightning.Domain.Channels.Commitments;

/// <summary>
/// BOLT 3 fee and trimming formulas (§Fee Calculation, §Trimmed Outputs), verbatim, for a <see cref="CommitmentSpec"/>.
/// </summary>
/// <remarks>
/// All results are whole satoshis unless the name says msat. HTLC transaction fees are 0 with <c>option_anchors</c>.
/// An HTLC is trimmed when <c>floor(amount_msat / 1000) &lt; holder dust_limit + HTLC tx fee</c>, where the fee is the
/// HTLC-timeout fee for an HTLC offered by the holder and the HTLC-success fee for one it received.
/// </remarks>
public static class CommitmentFees
{
    public const ulong CommitmentWeightNoAnchors = 724;
    public const ulong CommitmentWeightAnchors = 1124;
    public const ulong HtlcOutputWeight = 172;
    public const ulong HtlcTimeoutWeightNoAnchors = 663;
    public const ulong HtlcSuccessWeightNoAnchors = 703;
    public const ulong AnchorOutputSatoshis = 330;

    /// <summary>HTLC-timeout transaction fee: <c>floor(feerate * 663 / 1000)</c>, or 0 with anchors.</summary>
    public static ulong HtlcTimeoutFee(uint feeratePerKw, bool optionAnchors) =>
        optionAnchors ? 0 : feeratePerKw * HtlcTimeoutWeightNoAnchors / 1000;

    /// <summary>HTLC-success transaction fee: <c>floor(feerate * 703 / 1000)</c>, or 0 with anchors.</summary>
    public static ulong HtlcSuccessFee(uint feeratePerKw, bool optionAnchors) =>
        optionAnchors ? 0 : feeratePerKw * HtlcSuccessWeightNoAnchors / 1000;

    /// <summary>True when the HTLC produces no output in the holder's commitment (its value goes to fees).</summary>
    /// <param name="amountMsat">HTLC amount.</param>
    /// <param name="offeredByHolder">The holder of the commitment offered the HTLC (HTLC-timeout applies).</param>
    /// <param name="holderDustLimitSatoshis">The holder's <c>dust_limit_satoshis</c>.</param>
    /// <param name="feeratePerKw">The commitment's feerate.</param>
    /// <param name="optionAnchors">Whether <c>option_anchors</c> applies.</param>
    public static bool IsTrimmed(ulong amountMsat, bool offeredByHolder, ulong holderDustLimitSatoshis,
                                 uint feeratePerKw, bool optionAnchors)
    {
        var htlcTxFee = offeredByHolder
                            ? HtlcTimeoutFee(feeratePerKw, optionAnchors)
                            : HtlcSuccessFee(feeratePerKw, optionAnchors);
        return amountMsat / 1000 < checked(holderDustLimitSatoshis + htlcTxFee);
    }

    /// <summary>Number of HTLC outputs of the commitment (= <c>num_htlcs</c> of its <c>commitment_signed</c>).</summary>
    public static int UntrimmedHtlcCount(CommitmentSpec spec, ulong holderDustLimitSatoshis, bool optionAnchors) =>
        spec.Htlcs.Count(h => !IsTrimmed(h.AmountMsat, h.IsOfferedBy(spec.Holder), holderDustLimitSatoshis,
                                         spec.FeeratePerKw, optionAnchors));

    /// <summary>Sum (msat) of the HTLCs trimmed from the commitment: its dust exposure.</summary>
    public static ulong TrimmedHtlcTotalMsat(CommitmentSpec spec, ulong holderDustLimitSatoshis, bool optionAnchors) =>
        spec.Htlcs.Where(h => IsTrimmed(h.AmountMsat, h.IsOfferedBy(spec.Holder), holderDustLimitSatoshis,
                                        spec.FeeratePerKw, optionAnchors))
            .Aggregate(0UL, (sum, h) => checked(sum + h.AmountMsat));

    /// <summary>Base commitment fee: <c>floor(feerate * (724 | 1124 + 172 * untrimmed) / 1000)</c>.</summary>
    public static ulong BaseCommitmentFee(uint feeratePerKw, int untrimmedHtlcs, bool optionAnchors)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(untrimmedHtlcs);
        var weight = checked((optionAnchors ? CommitmentWeightAnchors : CommitmentWeightNoAnchors)
                           + HtlcOutputWeight * (ulong)untrimmedHtlcs);
        return checked(feeratePerKw * weight) / 1000;
    }

    /// <summary>Base commitment fee of <paramref name="spec"/>, trimmed with the holder's dust limit.</summary>
    public static ulong BaseCommitmentFee(CommitmentSpec spec, ulong holderDustLimitSatoshis, bool optionAnchors) =>
        BaseCommitmentFee(spec.FeeratePerKw, UntrimmedHtlcCount(spec, holderDustLimitSatoshis, optionAnchors),
                          optionAnchors);

    /// <summary>
    /// What the funder pays on top of its HTLCs for this commitment, in msat: the base fee plus both anchors
    /// (<c>2 * 330</c> sat) with <c>option_anchors</c>.
    /// </summary>
    public static ulong FunderCostMsat(CommitmentSpec spec, ulong holderDustLimitSatoshis, bool optionAnchors) =>
        checked((BaseCommitmentFee(spec, holderDustLimitSatoshis, optionAnchors)
               + (optionAnchors ? 2 * AnchorOutputSatoshis : 0)) * 1_000);
}