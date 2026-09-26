namespace NLightning.Domain.Onchain.Fees;

using Channels.Enums;

/// <summary>
/// The fee rules of our sweeps, claims and penalties (BOLT 5 plan §3.7, O6-T1). Pure: the caller asks
/// <c>IFeeService</c> for the estimate at <see cref="GetConfirmationTarget"/> and passes it in.
/// </summary>
/// <remarks>
/// Heights: a deadline is the first block height at which a competitor can take the output. Rates are sat per 1000
/// weight units, fees are absolute satoshis.
/// </remarks>
public sealed class SweepFeePolicy
{
    /// <summary>The <c>nSequence</c> of an input without a relative lock: final for BIP 68 and BIP 125 replaceable.</summary>
    public const uint RbfSequence = 0xFFFFFFFD;

    private readonly SweepFeePolicyOptions _options;

    public SweepFeePolicy(SweepFeePolicyOptions? options = null)
    {
        _options = options ?? new SweepFeePolicyOptions();
    }

    /// <summary>The settings in use.</summary>
    public SweepFeePolicyOptions Options => _options;

    /// <summary>
    /// The confirmation target to estimate with: <c>clamp(deadline - tip - safety, 1, MaxConfTarget)</c>, or
    /// <see cref="SweepFeePolicyOptions.SweepConfTarget"/> without a deadline.
    /// </summary>
    public uint GetConfirmationTarget(uint tipHeight, uint? deadlineHeight)
    {
        if (deadlineHeight is not { } deadline)
            return Math.Clamp(_options.SweepConfTarget, 1, _options.MaxConfTarget);

        var remaining = (long)deadline - tipHeight - _options.DeadlineSafetyBlocks;
        return (uint)Math.Clamp(remaining, 1, _options.MaxConfTarget);
    }

    /// <summary>The feerate to sign with for an estimate: never below <see cref="SweepFeePolicyOptions.MinFeeratePerKw"/>.</summary>
    public uint ApplyFloor(uint estimatePerKw) => Math.Max(estimatePerKw, _options.MinFeeratePerKw);

    /// <summary>
    /// The most a transaction spending <paramref name="inputValueSat"/> may pay: half of it for a sweep or claim; for a
    /// penalty whose deadline is within <see cref="SweepFeePolicyOptions.SecurityDelay"/> blocks, up to
    /// <see cref="SweepFeePolicyOptions.PenaltyMaxFeePerMille"/> (taking the funds from the cheater beats letting its
    /// CSV expire).
    /// </summary>
    public ulong GetMaxFee(ulong inputValueSat, bool isPenalty, uint tipHeight, uint? deadlineHeight)
    {
        var perMille = isPenalty && deadlineHeight is { } deadline && IsWithinSecurityDelay(tipHeight, deadline)
                           ? _options.PenaltyMaxFeePerMille
                           : _options.SweepMaxFeePerMille;
        return (ulong)(inputValueSat * (decimal)perMille / 1000);
    }

    /// <summary>
    /// The fee for a transaction of <paramref name="weight"/> spending <paramref name="inputValueSat"/> at the
    /// estimate for its target: floored, capped by <see cref="GetMaxFee"/>, and abandoned when the value does not pay
    /// its own fee at the floor rate (dust).
    /// </summary>
    public SweepFeeDecision Decide(ulong inputValueSat, long weight, uint estimatePerKw, bool isPenalty,
                                   uint tipHeight, uint? deadlineHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(weight);

        var floorFee = SweepWeights.FeeSat(_options.MinFeeratePerKw, weight);
        if (inputValueSat <= floorFee)
            return new SweepFeeDecision(_options.MinFeeratePerKw, floorFee, false, true);

        var feerate = ApplyFloor(estimatePerKw);
        var fee = SweepWeights.FeeSat(feerate, weight);
        var maxFee = Math.Max(GetMaxFee(inputValueSat, isPenalty, tipHeight, deadlineHeight), floorFee);
        if (fee <= maxFee)
            return new SweepFeeDecision(feerate, fee, false, false);

        return new SweepFeeDecision(FeeratePerKw(maxFee, weight), maxFee, true, false);
    }

    /// <summary>
    /// Whether an unconfirmed transaction first broadcast at <paramref name="broadcastHeight"/> is replaced now: after
    /// <see cref="SweepFeePolicyOptions.RbfIntervalBlocks"/> blocks while its deadline is still ahead, and after the
    /// sweep target for a transaction without a deadline.
    /// </summary>
    public bool ShouldBump(uint broadcastHeight, uint tipHeight, uint? deadlineHeight)
    {
        if (tipHeight < broadcastHeight)
            return false;

        var waited = tipHeight - broadcastHeight;
        if (deadlineHeight is not { } deadline)
            return waited >= _options.SweepConfTarget;

        return waited >= _options.RbfIntervalBlocks && tipHeight < deadline;
    }

    /// <summary>
    /// The absolute fee of a replacement (BIP 125 rules 3 and 4): at least the old fee times the multiplier, and at
    /// least the old fee plus the relay fee of the replacement's own size.
    /// </summary>
    public ulong GetReplacementFee(ulong oldFeeSat, long newVirtualSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(newVirtualSize);

        var multiplied = (ulong)Math.Ceiling(oldFeeSat * (decimal)_options.RbfFeeMultiplierPerMille / 1000);
        var relayIncrement = (ulong)Math.Ceiling((decimal)_options.MinRelayFeePerKvB * newVirtualSize / 1000);
        return Math.Max(multiplied, oldFeeSat + relayIncrement);
    }

    /// <summary>
    /// The fee of an RBF replacement of an unconfirmed sweep, claim or penalty (O6-T1, <c>SweepScheduler</c>): the
    /// larger of the BIP 125 minimum (<see cref="GetReplacementFee"/>) and the fee at the current estimate for the
    /// deadline's target, never above <see cref="GetMaxFee"/> nor leaving less than <paramref name="dustSat"/> in the
    /// output. Null when even the BIP 125 minimum does not fit under that cap: the transaction is kept as it is.
    /// </summary>
    /// <param name="inputValueSat">The value of the inputs (the replacement spends the same ones).</param>
    /// <param name="oldFeeSat">The fee of the transaction being replaced.</param>
    /// <param name="weight">The replacement's weight (at most the old one's plus a byte per signature).</param>
    /// <param name="estimatePerKw">The estimate for <see cref="GetConfirmationTarget"/> of the deadline.</param>
    /// <param name="isPenalty">True for a penalty (its cap rises to <see cref="SweepFeePolicyOptions.PenaltyMaxFeePerMille"/>
    /// near its deadline).</param>
    /// <param name="tipHeight">The current tip.</param>
    /// <param name="deadlineHeight">The height at which a competitor can take an output, if any.</param>
    /// <param name="dustSat">The dust threshold of the output's script.</param>
    public SweepFeeDecision? DecideReplacement(ulong inputValueSat, ulong oldFeeSat, long weight, uint estimatePerKw,
                                               bool isPenalty, uint tipHeight, uint? deadlineHeight, ulong dustSat)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(weight);
        if (inputValueSat <= dustSat)
            return null;

        var minimum = GetReplacementFee(oldFeeSat, SweepWeights.VirtualSize(weight));
        var atEstimate = SweepWeights.FeeSat(ApplyFloor(estimatePerKw), weight);
        var cap = Math.Min(GetMaxFee(inputValueSat, isPenalty, tipHeight, deadlineHeight), inputValueSat - dustSat);
        var wanted = Math.Max(minimum, atEstimate);
        var fee = Math.Min(wanted, cap);
        if (fee < minimum)
            return null;

        return new SweepFeeDecision(FeeratePerKw(fee, weight), fee, fee < wanted, false);
    }

    /// <summary>
    /// B5-REV-08: split a batched penalty into per-output transactions once a revoked output's deadline is within
    /// <see cref="SweepFeePolicyOptions.SecurityDelay"/> blocks.
    /// </summary>
    public bool ShouldSplitPenalty(uint tipHeight, uint deadlineHeight) =>
        IsWithinSecurityDelay(tipHeight, deadlineHeight);

    /// <summary>
    /// The deadline of a revoked commitment's output (§3.7): the peer's <c>to_local</c> (and the output of its
    /// second-level transaction) at <c>confirmation height + to_self_delay</c>; an HTLC the peer offered at its
    /// <c>cltv_expiry</c> (its HTLC-timeout path); an HTLC we offered at once (the cheater may know the preimage and
    /// use its HTLC-success path in the next block).
    /// </summary>
    /// <param name="htlcDirection">Null for a delayed output; the HTLC direction from our point of view otherwise.</param>
    /// <param name="confirmationHeight">The height of the block that confirmed the transaction holding the output.</param>
    /// <param name="toSelfDelay">The CSV delay of a delayed output (our <c>Local.ToSelfDelay</c>).</param>
    /// <param name="cltvExpiry">The HTLC's expiry.</param>
    /// <param name="tipHeight">The current tip.</param>
    public static uint GetRevokedOutputDeadline(HtlcDirection? htlcDirection, uint confirmationHeight,
                                                ushort toSelfDelay, uint cltvExpiry, uint tipHeight) =>
        htlcDirection switch
        {
            null => confirmationHeight + toSelfDelay,
            HtlcDirection.Incoming => cltvExpiry,
            _ => tipHeight + 1
        };

    /// <summary>The feerate (sat/kw, rounded down) that <paramref name="feeSat"/> pays for <paramref name="weight"/>.</summary>
    public static uint FeeratePerKw(ulong feeSat, long weight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(weight);
        return (uint)Math.Min(uint.MaxValue, feeSat * 1000 / (ulong)weight);
    }

    private bool IsWithinSecurityDelay(uint tipHeight, uint deadlineHeight) =>
        (long)deadlineHeight - tipHeight <= _options.SecurityDelay;
}

/// <summary>
/// The result of <see cref="SweepFeePolicy.Decide"/>.
/// </summary>
/// <param name="FeeratePerKw">The feerate the fee pays.</param>
/// <param name="FeeSat">The absolute fee.</param>
/// <param name="Capped">True when the estimate was above the cap and the fee was lowered to it.</param>
/// <param name="Abandon">True when the output is worth no more than its own fee at the floor rate (dust): record it
/// abandoned and log it.</param>
public sealed record SweepFeeDecision(uint FeeratePerKw, ulong FeeSat, bool Capped, bool Abandon);