namespace NLightning.Domain.Node.Options;

/// <summary>
/// When we send <c>update_fee</c> on the channels we fund (BOLT 2 "Updating Fees", BOLT2 plan N9-T1). Bound from the
/// <c>Node:FeeUpdates</c> configuration section (it is <see cref="NodeOptions.FeeUpdates"/>).
/// </summary>
/// <remarks>
/// Every <see cref="Interval"/> the fee scheduler reads the node's fee estimate (sat/kw) and, for each open channel we
/// fund, moves the commitment feerate to it (times <see cref="NonAnchorFeerateMarginPercent"/> without
/// <c>option_anchors</c>) when it differs by at least <see cref="ThresholdPercent"/> (hysteresis, so
/// a noisy estimate does not cause an update every round), clamped to
/// [<see cref="MinFeeratePerKw"/>, <see cref="MaxFeeratePerKw"/>] (<see cref="MaxAnchorFeeratePerKw"/> for
/// <c>option_anchors</c> channels, which can be bumped with CPFP). Call <see cref="GetValidationErrors"/> (through
/// <see cref="NodeOptions.GetValidationErrors"/>) at startup.
/// </remarks>
public class FeeUpdateOptions
{
    /// <summary>
    /// BOLT 3: the lowest feerate a node should accept (253 sat/kw, i.e. 1 sat/vbyte rounded up).
    /// </summary>
    public const uint FeeratePerKwFloor = 253;

    /// <summary>
    /// Sends <c>update_fee</c> on the channels we fund. Off: our commitment feerate stays where the channel opened.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often the estimate is compared with every channel's feerate.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The relative change (percent of the channel's current feerate) the estimate must reach before we update: 20
    /// means we move up when the estimate is at least 120 % of the feerate and down at 80 % or less. A feerate below
    /// <see cref="MinFeeratePerKw"/> is always raised.
    /// </summary>
    public uint ThresholdPercent { get; set; } = 20;

    /// <summary>
    /// The safety margin (percent of the estimate) we aim for on a channel without <c>option_anchors</c>: 200 means
    /// twice the estimate. BOLT 2: the funder SHOULD keep the feerate sufficient "by a significant margin"; a legacy
    /// commitment cannot be bumped with CPFP, so it must confirm at the feerate it was signed with. Because the margin is
    /// part of the target, a falling estimate never takes the feerate below margin x estimate. Anchor channels use the
    /// estimate as is (CPFP on the anchor adds the rest). At least 100; the result is still clamped to
    /// <see cref="MaxFeeratePerKw"/>.
    /// </summary>
    public uint NonAnchorFeerateMarginPercent { get; set; } = 200;

    /// <summary>
    /// The lowest feerate we ever set; never below <see cref="FeeratePerKwFloor"/>.
    /// </summary>
    public uint MinFeeratePerKw { get; set; } = FeeratePerKwFloor;

    /// <summary>
    /// The highest feerate we set on a channel without <c>option_anchors</c> (200 sat/vbyte by default). Our own
    /// receive limit for <c>update_fee</c> is 250,000 sat/kw.
    /// </summary>
    public uint MaxFeeratePerKw { get; set; } = 50_000;

    /// <summary>
    /// The highest feerate we set on an <c>option_anchors</c> channel (10 sat/vbyte, as LND's
    /// <c>max-commit-fee-rate-anchors</c>): the commitment only has to enter the mempool, CPFP on the anchor does the
    /// rest.
    /// </summary>
    public uint MaxAnchorFeeratePerKw { get; set; } = 2_500;

    /// <summary>
    /// Returns every configuration error; empty when valid.
    /// </summary>
    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        if (Interval <= TimeSpan.Zero)
            errors.Add($"FeeUpdates:{nameof(Interval)} must be positive.");
        if (ThresholdPercent is 0 or >= 100)
            errors.Add($"FeeUpdates:{nameof(ThresholdPercent)} must be between 1 and 99.");
        if (NonAnchorFeerateMarginPercent < 100)
            errors.Add($"FeeUpdates:{nameof(NonAnchorFeerateMarginPercent)} must be at least 100.");
        if (MinFeeratePerKw < FeeratePerKwFloor)
            errors.Add($"FeeUpdates:{nameof(MinFeeratePerKw)} must be at least {FeeratePerKwFloor} sat/kw.");
        if (MaxFeeratePerKw < MinFeeratePerKw)
            errors.Add($"FeeUpdates:{nameof(MaxFeeratePerKw)} must be at least {nameof(MinFeeratePerKw)}.");
        if (MaxAnchorFeeratePerKw < MinFeeratePerKw)
            errors.Add($"FeeUpdates:{nameof(MaxAnchorFeeratePerKw)} must be at least {nameof(MinFeeratePerKw)}.");

        return errors;
    }
}