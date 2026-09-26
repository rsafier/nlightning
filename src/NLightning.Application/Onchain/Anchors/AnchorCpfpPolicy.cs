namespace NLightning.Application.Onchain.Anchors;

using Domain.Bitcoin.Transactions.Constants;
using Domain.Onchain.Fees;

/// <summary>
/// The fee rules of an anchor CPFP (BOLT 5 plan O7-T2, B5-FAIL-06). Pure: the caller passes the estimate for
/// <see cref="GetConfirmationTarget"/>. Rates are sat per 1000 weight units, fees absolute satoshis, and the package is
/// the commitment plus its child (bitcoind's ancestor feerate).
/// </summary>
/// <remarks>
/// <para>First child: none while the commitment alone pays the floored estimate; otherwise the child pays
/// <c>ceil(rate * (commitment weight + child weight) / 1000) - commitment fee</c>, at least its own weight at the floor
/// rate, capped at <see cref="GetFeeCap"/>.</para>
/// <para>Replacement (RBF, when <see cref="SweepFeePolicy.ShouldBump"/> says the child waited long enough): the larger of
/// the BIP 125 minimum over the old child's fee (<see cref="SweepFeePolicy.GetReplacementFee"/>) and the fee for the
/// current estimate, capped; none when the old child already pays the estimate or the minimum does not fit under the
/// cap.</para>
/// </remarks>
public sealed class AnchorCpfpPolicy
{
    private readonly AnchorCpfpOptions _options;

    public AnchorCpfpPolicy(SweepFeePolicy feePolicy, AnchorCpfpOptions? options = null)
    {
        FeePolicy = feePolicy ?? throw new ArgumentNullException(nameof(feePolicy));
        _options = options ?? new AnchorCpfpOptions();
    }

    /// <summary>The shared sweep fee rules (floor, targets, RBF interval and increments).</summary>
    public SweepFeePolicy FeePolicy { get; }

    /// <summary>The value of one anchor output (330 sat).</summary>
    public static ulong AnchorSat => (ulong)TransactionConstants.AnchorOutputAmount.Satoshi;

    /// <summary>
    /// The deadline of a commitment: the earliest <c>cltv_expiry</c> of its HTLCs (from then on the peer can time out
    /// an HTLC we received, and an HTLC we offered must be timed out on chain before its upstream expires), null
    /// without HTLCs.
    /// </summary>
    public static uint? GetDeadline(IEnumerable<uint> htlcExpiries)
    {
        ArgumentNullException.ThrowIfNull(htlcExpiries);

        uint? deadline = null;
        foreach (var expiry in htlcExpiries)
            if (deadline is null || expiry < deadline)
                deadline = expiry;

        return deadline;
    }

    /// <summary>
    /// The confirmation target to estimate with: the sweep policy's for a deadline, else
    /// <see cref="AnchorCpfpOptions.NoDeadlineConfTarget"/>.
    /// </summary>
    public uint GetConfirmationTarget(uint tipHeight, uint? deadlineHeight) =>
        deadlineHeight is null
            ? Math.Clamp(_options.NoDeadlineConfTarget, 1, FeePolicy.Options.MaxConfTarget)
            : FeePolicy.GetConfirmationTarget(tipHeight, deadlineHeight);

    /// <summary>The most a child may pay for a commitment carrying <paramref name="stakeSat"/> of ours.</summary>
    public ulong GetFeeCap(ulong stakeSat) =>
        Math.Max((ulong)(stakeSat * (decimal)_options.MaxFeePerMilleOfStake / 1000), _options.MinFeeCapSat);

    /// <summary>The feerate (sat/kw, rounded down) of a transaction paying <paramref name="feeSat"/> for
    /// <paramref name="weight"/>.</summary>
    public static uint FeeratePerKw(ulong feeSat, long weight) => SweepFeePolicy.FeeratePerKw(feeSat, weight);

    /// <summary>
    /// The fee a first child must pay, or null when the commitment alone already pays the floored estimate.
    /// </summary>
    /// <param name="commitmentFeeSat">The commitment's fee (funding amount minus its outputs).</param>
    /// <param name="commitmentWeight">The signed commitment's weight.</param>
    /// <param name="childWeight">The child's estimated weight.</param>
    /// <param name="estimatePerKw">The estimate for the commitment's target.</param>
    /// <param name="feeCapSat">The cap (<see cref="GetFeeCap"/>).</param>
    public AnchorChildFeeDecision? DecideChild(ulong commitmentFeeSat, long commitmentWeight, long childWeight,
                                               uint estimatePerKw, ulong feeCapSat)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(commitmentWeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(childWeight);

        var rate = FeePolicy.ApplyFloor(estimatePerKw);
        if (FeeratePerKw(commitmentFeeSat, commitmentWeight) >= rate)
            return null;

        var wanted = GetChildFeeForRate(rate, commitmentFeeSat, commitmentWeight, childWeight);
        var fee = Math.Min(wanted, Math.Max(feeCapSat, ChildFloorFee(childWeight)));
        return new AnchorChildFeeDecision(fee, rate, PackageFeerate(commitmentFeeSat, commitmentWeight, fee,
                                                                    childWeight), fee < wanted);
    }

    /// <summary>
    /// The fee of an RBF replacement of an unconfirmed child, or null when none is due (the old child already pays the
    /// estimate) or possible (the BIP 125 minimum is above the cap).
    /// </summary>
    /// <param name="commitmentFeeSat">The commitment's fee.</param>
    /// <param name="commitmentWeight">The signed commitment's weight.</param>
    /// <param name="childWeight">The replacement's estimated weight.</param>
    /// <param name="estimatePerKw">The estimate for the commitment's target now.</param>
    /// <param name="oldChildFeeSat">The replaced child's fee (an upper bound is fine: it only raises the minimum).</param>
    /// <param name="feeCapSat">The cap (<see cref="GetFeeCap"/>).</param>
    public AnchorChildFeeDecision? DecideReplacement(ulong commitmentFeeSat, long commitmentWeight, long childWeight,
                                                     uint estimatePerKw, ulong oldChildFeeSat, ulong feeCapSat)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(commitmentWeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(childWeight);

        var rate = FeePolicy.ApplyFloor(estimatePerKw);
        var atEstimate = GetChildFeeForRate(rate, commitmentFeeSat, commitmentWeight, childWeight);
        var minimum = FeePolicy.GetReplacementFee(oldChildFeeSat, SweepWeights.VirtualSize(childWeight));
        var wanted = Math.Max(minimum, atEstimate);
        var fee = Math.Min(wanted, feeCapSat);
        if (fee < minimum)
            return null;

        return new AnchorChildFeeDecision(fee, rate, PackageFeerate(commitmentFeeSat, commitmentWeight, fee,
                                                                    childWeight), fee < wanted);
    }

    /// <summary>
    /// Whether the old child's package already pays <paramref name="estimatePerKw"/> (floored): then it is kept.
    /// </summary>
    public bool PackagePays(ulong commitmentFeeSat, long commitmentWeight, ulong childFeeSat, long childWeight,
                            uint estimatePerKw) =>
        PackageFeerate(commitmentFeeSat, commitmentWeight, childFeeSat, childWeight)
     >= FeePolicy.ApplyFloor(estimatePerKw);

    /// <summary>
    /// Whether sweeping <paramref name="anchorCount"/> anchors anyone may spend pays for itself: the output after the
    /// fee at the floored estimate is at least <paramref name="dustSat"/>, and the fee is at most the sweep cap
    /// (<see cref="SweepFeePolicyOptions.SweepMaxFeePerMille"/> of the anchors' value). One anchor to a P2WPKH output
    /// never qualifies (330 sat minus even the floor fee is below 294); two do only below about 2.3 sat/vB.
    /// </summary>
    public AnchorSweepDecision DecideAnchorSweep(int anchorCount, long weight, uint estimatePerKw, ulong dustSat)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(anchorCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(weight);

        var value = AnchorSat * (ulong)anchorCount;
        var rate = FeePolicy.ApplyFloor(estimatePerKw);
        var fee = SweepWeights.FeeSat(rate, weight);
        var economical = fee < value && value - fee >= dustSat
                      && fee * 1000 <= value * FeePolicy.Options.SweepMaxFeePerMille;
        return new AnchorSweepDecision(economical, fee, rate);
    }

    /// <summary>The package feerate (sat/kw, rounded down) of a commitment and its child.</summary>
    public static uint PackageFeerate(ulong commitmentFeeSat, long commitmentWeight, ulong childFeeSat,
                                      long childWeight) =>
        FeeratePerKw(commitmentFeeSat + childFeeSat, commitmentWeight + childWeight);

    private ulong GetChildFeeForRate(uint rate, ulong commitmentFeeSat, long commitmentWeight, long childWeight)
    {
        // Rounded up, so the package never lands a fraction below the rate
        var packageFee = ((ulong)rate * (ulong)(commitmentWeight + childWeight) + 999) / 1000;
        var needed = packageFee > commitmentFeeSat ? packageFee - commitmentFeeSat : 0;
        return Math.Max(needed, ChildFloorFee(childWeight));
    }

    private ulong ChildFloorFee(long childWeight) =>
        (FeePolicy.Options.MinFeeratePerKw * (ulong)childWeight + 999) / 1000;
}

/// <summary>The result of <see cref="AnchorCpfpPolicy.DecideChild"/> or <see cref="AnchorCpfpPolicy.DecideReplacement"/>.
/// </summary>
/// <param name="FeeSat">The child's absolute fee.</param>
/// <param name="TargetFeeratePerKw">The package feerate aimed at (the floored estimate).</param>
/// <param name="PackageFeeratePerKw">The package feerate this fee gives.</param>
/// <param name="Capped">True when the cap lowered the fee below what the estimate asks.</param>
public sealed record AnchorChildFeeDecision(ulong FeeSat, uint TargetFeeratePerKw, uint PackageFeeratePerKw,
                                            bool Capped);

/// <summary>The result of <see cref="AnchorCpfpPolicy.DecideAnchorSweep"/>.</summary>
/// <param name="Economical">True when the sweep is worth sending.</param>
/// <param name="FeeSat">Its fee.</param>
/// <param name="FeeratePerKw">The feerate it would pay.</param>
public sealed record AnchorSweepDecision(bool Economical, ulong FeeSat, uint FeeratePerKw);