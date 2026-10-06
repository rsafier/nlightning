namespace NLightning.Domain.Channels.Acceptance;

using Money;
using ValueObjects;

/// <summary>
/// The rules of external open decisions (NL-1180): how several deciders' answers combine (LND's
/// <c>chanacceptor.mergeResponse</c>) and which of an acceptance's values can apply to the parameters we announce.
/// </summary>
public static class ChannelOpenDecisionRules
{
    /// <summary>The longest rejection text sent to the opener (LND's <c>maxErrorLength</c>).</summary>
    public const int MaxErrorLength = 500;

    /// <summary>The BOLT 2 limit on <c>max_accepted_htlcs</c>.</summary>
    public const ushort MaxAcceptedHtlcsLimit = 483;

    /// <summary>
    /// Combines two acceptances: a value set by both must be equal (LND's merge); <c>zero_conf</c> is set when either
    /// sets it. Returns the error when they disagree.
    /// </summary>
    public static string? TryMerge(ChannelOpenDecision current, ChannelOpenDecision next,
                                   out ChannelOpenDecision merged)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(next);
        merged = current;
        if (Conflicts(current.ToSelfDelay, next.ToSelfDelay))
            return "acceptors disagree on csv_delay";
        if (Conflicts(current.MaxAcceptedHtlcs, next.MaxAcceptedHtlcs))
            return "acceptors disagree on max_htlc_count";
        if (Conflicts(current.MinimumDepth, next.MinimumDepth))
            return "acceptors disagree on min_accept_depth";
        if (Conflicts(current.ChannelReserve, next.ChannelReserve))
            return "acceptors disagree on reserve_sat";
        if (Conflicts(current.HtlcMinimum, next.HtlcMinimum))
            return "acceptors disagree on min_htlc_in";
        if (Conflicts(current.MaxHtlcValueInFlight, next.MaxHtlcValueInFlight))
            return "acceptors disagree on in_flight_max_msat";
        if (current.UpfrontShutdownScript is { } a && next.UpfrontShutdownScript is { } b && !a.Equals(b))
            return "acceptors disagree on upfront_shutdown";

        merged = new ChannelOpenDecision
        {
            Accept = true,
            ToSelfDelay = current.ToSelfDelay ?? next.ToSelfDelay,
            MaxAcceptedHtlcs = current.MaxAcceptedHtlcs ?? next.MaxAcceptedHtlcs,
            MinimumDepth = current.MinimumDepth ?? next.MinimumDepth,
            ChannelReserve = current.ChannelReserve ?? next.ChannelReserve,
            HtlcMinimum = current.HtlcMinimum ?? next.HtlcMinimum,
            MaxHtlcValueInFlight = current.MaxHtlcValueInFlight ?? next.MaxHtlcValueInFlight,
            UpfrontShutdownScript = current.UpfrontShutdownScript ?? next.UpfrontShutdownScript,
            ZeroConf = current.ZeroConf || next.ZeroConf
        };
        return merged.ZeroConf && merged.MinimumDepth is > 0
                   ? "zero_conf with a non-zero min_accept_depth"
                   : null;
    }

    /// <summary>
    /// Applies an acceptance's values to the parameters we announce. Returns the error that refuses the open when a
    /// value cannot apply here: <c>zero_conf</c> (not supported), a <c>reserve_sat</c> on a dual-funded open (BOLT 2
    /// fixes it), an upfront shutdown script on a dual-funded open, a reserve below either dust limit or not below the
    /// capacity, more than 483 HTLCs, an HTLC minimum not below the capacity.
    /// </summary>
    /// <param name="decision">The (merged) acceptance.</param>
    /// <param name="request">The open it answers.</param>
    /// <param name="local">The parameters the node would announce.</param>
    /// <param name="minimumDepth">The depth the node would ask for.</param>
    /// <param name="capacity">The channel's total funding.</param>
    /// <param name="newLocal">The parameters to announce.</param>
    /// <param name="newMinimumDepth">The depth to ask for.</param>
    public static string? TryApply(ChannelOpenDecision decision, ChannelOpenRequest request, ChannelParty local,
                                   uint minimumDepth, LightningMoney capacity, out ChannelParty newLocal,
                                   out uint newMinimumDepth)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(request);
        newLocal = local;
        newMinimumDepth = minimumDepth;
        if (decision.ZeroConf)
            return "zero-conf channels are not supported";
        if (decision.MinimumDepth is 0)
            return "min_accept_depth 0 (zero-conf) is not supported";
        if (decision.ToSelfDelay is 0)
            return "csv_delay must be at least 1";
        if (decision.MaxAcceptedHtlcs is 0 or > MaxAcceptedHtlcsLimit)
            return $"max_htlc_count must be 1 to {MaxAcceptedHtlcsLimit}";
        if (decision.MaxHtlcValueInFlight is { } inFlight && inFlight == LightningMoney.Zero)
            return "in_flight_max_msat must be positive";
        if (decision.HtlcMinimum is { } htlcMinimum && htlcMinimum >= capacity)
            return "min_htlc_in must be below the channel capacity";
        if (request.DualFunded && decision.ChannelReserve is not null)
            return "reserve_sat cannot apply to a dual-funded channel (BOLT 2 fixes its reserve)";
        if (request.DualFunded && decision.UpfrontShutdownScript is not null)
            return "upfront_shutdown is not supported on a dual-funded open";
        if (decision.ChannelReserve is { } reserve)
        {
            if (reserve < request.DustLimit || reserve < local.DustLimitAmount)
                return "reserve lower than proposed dust limit";
            if (reserve >= capacity)
                return "reserve_sat must be below the channel capacity";
        }

        newLocal = new ChannelParty(local.DustLimitAmount, decision.ChannelReserve ?? local.ChannelReserveAmount,
                                    decision.HtlcMinimum ?? local.HtlcMinimumAmount,
                                    decision.MaxAcceptedHtlcs ?? local.MaxAcceptedHtlcs,
                                    decision.MaxHtlcValueInFlight ?? local.MaxHtlcValueInFlight,
                                    decision.ToSelfDelay ?? local.ToSelfDelay,
                                    decision.UpfrontShutdownScript ?? local.UpfrontShutdownScript);
        newMinimumDepth = decision.MinimumDepth ?? minimumDepth;
        return null;
    }

    /// <summary>A rejection text cut to <see cref="MaxErrorLength"/>.</summary>
    public static string Truncate(string? error) =>
        string.IsNullOrEmpty(error) ? ChannelOpenDecision.GenericRejection
        : error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;

    private static bool Conflicts<T>(T? a, T? b) where T : struct => a is { } x && b is { } y && !x.Equals(y);

    private static bool Conflicts(LightningMoney? a, LightningMoney? b) => a is { } x && b is { } y && x != y;
}