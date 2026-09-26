namespace NLightning.Application.Channels.Fees;

using Domain.Channels.Commitments;
using Domain.Exceptions;
using Domain.Node.Options;

/// <summary>
/// When the funder should send <c>update_fee</c>, and with which feerate (BOLT2 plan N9-T1; B2-FEE-S01..S03,
/// B2-DUST-05). Pure: it reads a commitment snapshot and changes nothing.
/// </summary>
/// <remarks>
/// <para>BOLT 2: the node responsible for the fee SHOULD keep the feerate sufficient "by a significant margin"; the
/// other MUST NOT send <c>update_fee</c>. The target is the estimate (times
/// <see cref="FeeUpdateOptions.NonAnchorFeerateMarginPercent"/> without <c>option_anchors</c>) clamped to
/// [<see cref="FeeUpdateOptions.MinFeeratePerKw"/>, max] (max = <see cref="FeeUpdateOptions.MaxAnchorFeeratePerKw"/>
/// with <c>option_anchors</c>, else <see cref="FeeUpdateOptions.MaxFeeratePerKw"/>), never below 253 sat/kw; we move
/// to it only when it differs from the channel's latest feerate by at least
/// <see cref="FeeUpdateOptions.ThresholdPercent"/> (or the feerate is below the minimum).</para>
/// <para>An increase must be sendable: the engine's sender rule (B2-FEE-R03, which the peer enforces: we must pay the
/// new fee above our reserve on its next commitment) and, without anchors, the dust exposure limit at the new feerate
/// (BOLT 2 "MAY NOT send update_fee"). When the target is not, the highest sendable feerate between the current one
/// and the target is used instead (feerate cost and dust exposure only grow with the feerate), or nothing when there is
/// none. A decrease is always sendable.</para>
/// </remarks>
public static class FeeUpdatePolicy
{
    /// <summary>
    /// The feerate we aim for given an estimate: without <c>option_anchors</c> the estimate times
    /// <see cref="FeeUpdateOptions.NonAnchorFeerateMarginPercent"/> (the BOLT 2 "significant margin"), then clamped to
    /// the configured bounds and the 253 sat/kw floor.
    /// </summary>
    public static uint TargetFeeratePerKw(uint estimatePerKw, bool optionAnchors, FeeUpdateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var min = Math.Max(options.MinFeeratePerKw, FeeUpdateOptions.FeeratePerKwFloor);
        var max = Math.Max(min, optionAnchors ? options.MaxAnchorFeeratePerKw : options.MaxFeeratePerKw);
        var withMargin = optionAnchors
                             ? estimatePerKw
                             : (ulong)estimatePerKw * Math.Max(options.NonAnchorFeerateMarginPercent, 100U) / 100;
        return (uint)Math.Clamp(withMargin, min, max);
    }

    /// <summary>
    /// The hysteresis: true when <paramref name="targetPerKw"/> differs from <paramref name="currentPerKw"/> by at
    /// least <see cref="FeeUpdateOptions.ThresholdPercent"/> of the current feerate, or raises a feerate that is below
    /// the minimum.
    /// </summary>
    public static bool ShouldAdjust(uint currentPerKw, uint targetPerKw, FeeUpdateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (targetPerKw == currentPerKw)
            return false;
        if (targetPerKw > currentPerKw
         && currentPerKw < Math.Max(options.MinFeeratePerKw, FeeUpdateOptions.FeeratePerKwFloor))
            return true;

        var delta = (ulong)(targetPerKw > currentPerKw ? targetPerKw - currentPerKw : currentPerKw - targetPerKw);
        return delta * 100 >= (ulong)currentPerKw * options.ThresholdPercent;
    }

    /// <summary>
    /// Decides the <c>update_fee</c> for a channel.
    /// </summary>
    /// <param name="commitments">The channel's commitment state.</param>
    /// <param name="estimatePerKw">The node's fee estimate (sat/kw); 0 means none.</param>
    /// <param name="options">The fee update options.</param>
    /// <param name="maxDustMsat">The channel's dust exposure limit (<see cref="DustExposurePolicy.Resolve"/>), or
    /// null for none.</param>
    public static FeeUpdateDecision Decide(ChannelCommitments commitments, uint estimatePerKw,
                                           FeeUpdateOptions options, ulong? maxDustMsat)
    {
        ArgumentNullException.ThrowIfNull(commitments);
        ArgumentNullException.ThrowIfNull(options);

        var current = commitments.LatestFeeratePerKw;
        if (!commitments.Params.LocalIsFunder)
            return FeeUpdateDecision.None(current, current, "we are not the funder (B2-FEE-S02)");
        if (estimatePerKw == 0)
            return FeeUpdateDecision.None(current, current, "no fee estimate");

        var target = TargetFeeratePerKw(estimatePerKw, commitments.Params.OptionAnchors, options);
        if (!ShouldAdjust(current, target, options))
            return FeeUpdateDecision.None(current, target, "within the threshold");

        if (target < current || CanSend(commitments, target, maxDustMsat, out _))
            return FeeUpdateDecision.Send(current, target, target, null);

        // The highest sendable feerate in (current, target): both constraints are monotonic in the feerate
        CanSend(commitments, target, maxDustMsat, out var reason);
        uint low = current, high = target;
        while (high - low > 1)
        {
            var mid = low + (high - low) / 2;
            if (CanSend(commitments, mid, maxDustMsat, out _))
                low = mid;
            else
                high = mid;
        }

        return low > current
                   ? FeeUpdateDecision.Send(current, target, low, $"capped: {reason}")
                   : FeeUpdateDecision.None(current, target, $"cannot raise the feerate: {reason}");
    }

    /// <summary>
    /// True when we may send <c>update_fee</c> with <paramref name="feeratePerKw"/>: the engine's sender rules accept
    /// it and, without anchors, it does not raise the dust exposure of either commitment over the limit.
    /// </summary>
    public static bool CanSend(ChannelCommitments commitments, uint feeratePerKw, ulong? maxDustMsat,
                               out string? reason)
    {
        ArgumentNullException.ThrowIfNull(commitments);
        try
        {
            commitments.SendFee(feeratePerKw);
        }
        catch (CommitmentRefusedException e)
        {
            reason = e.Message;
            return false;
        }

        if (maxDustMsat is { } maxDust
         && DustExposurePolicy.CheckFeeIncrease(commitments, feeratePerKw, maxDust) is { } excess)
        {
            reason = $"B2-DUST-05: {excess}";
            return false;
        }

        reason = null;
        return true;
    }
}

/// <summary>
/// The outcome of <see cref="FeeUpdatePolicy.Decide"/>.
/// </summary>
/// <param name="CurrentFeeratePerKw">The channel's latest feerate (pending updates included).</param>
/// <param name="TargetFeeratePerKw">The feerate the estimate asks for (clamped).</param>
/// <param name="FeeratePerKw">The feerate to send, or null for no update.</param>
/// <param name="Reason">Why nothing is sent, or why the feerate is below the target.</param>
public sealed record FeeUpdateDecision(uint CurrentFeeratePerKw, uint TargetFeeratePerKw, uint? FeeratePerKw,
                                       string? Reason)
{
    public bool ShouldSend => FeeratePerKw.HasValue;

    public static FeeUpdateDecision None(uint current, uint target, string reason) => new(current, target, null, reason);

    public static FeeUpdateDecision Send(uint current, uint target, uint feerate, string? reason) =>
        new(current, target, feerate, reason);
}