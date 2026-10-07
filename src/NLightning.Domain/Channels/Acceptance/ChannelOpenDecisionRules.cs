namespace NLightning.Domain.Channels.Acceptance;

using Closing;
using Domain.Enums;
using Money;
using Node.Options;
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
    /// Applies an acceptance's values to the parameters we announce, as LND's funding manager applies a
    /// <c>ChannelAcceptResponse</c> (NL-1181). Returns the error that refuses the open when a value cannot apply here:
    /// <list type="bullet">
    /// <item><c>zero_conf</c> applies only to an open whose <c>channel_type</c> has
    /// <c>option_zeroconf</c> (then we ask for depth 0); an acceptance of such an open without it is LND's "channel
    /// acceptor blocked zero-conf channel negotiation", and a zero-conf answer to an open without that type is refused
    /// (LND would turn it into a zero-conf channel through <c>option_scid_alias</c>; NLightning has no zero-conf
    /// channels of its own), as is a zero-conf answer with a non-zero depth; <c>min_accept_depth</c> 0 without
    /// <c>zero_conf</c> is refused (LND reads 0 as "unset", <see cref="ChannelOpenDecision.MinimumDepth"/>);</item>
    /// <item><c>reserve_sat</c> below either dust limit or not below the capacity; on a dual-funded open BOLT 2 fixes
    /// the reserve (1 % of the total, at least the dust limit), so a <c>reserve_sat</c> applies when that reserve
    /// already meets it and refuses the open otherwise;</item>
    /// <item><c>upfront_shutdown</c> without <c>option_upfront_shutdown_script</c> negotiated (LND's
    /// <c>errUpfrontShutdownScriptNotSupported</c>) or not a <c>shutdown</c> form the features allow (checked when
    /// <paramref name="negotiatedFeatures"/> is given); on a dual-funded open it goes in <c>accept_channel2</c>;</item>
    /// <item><c>csv_delay</c> 0, <c>max_htlc_count</c> above 483, <c>in_flight_max_msat</c> 0, <c>min_htlc_in</c>
    /// not below the capacity.</item>
    /// </list>
    /// </summary>
    /// <param name="decision">The (merged) acceptance.</param>
    /// <param name="request">The open it answers.</param>
    /// <param name="local">The parameters the node would announce.</param>
    /// <param name="minimumDepth">The depth the node would ask for.</param>
    /// <param name="capacity">The channel's total funding.</param>
    /// <param name="newLocal">The parameters to announce.</param>
    /// <param name="newMinimumDepth">The depth to ask for.</param>
    /// <param name="negotiatedFeatures">The features negotiated with the opener (null: the upfront script's feature
    /// and form are not checked here).</param>
    public static string? TryApply(ChannelOpenDecision decision, ChannelOpenRequest request, ChannelParty local,
                                   uint minimumDepth, LightningMoney capacity, out ChannelParty newLocal,
                                   out uint newMinimumDepth, FeatureOptions? negotiatedFeatures = null)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(request);
        newLocal = local;
        newMinimumDepth = minimumDepth;

        // Only zero_conf accepts a zero-conf channel: in LND min_accept_depth 0 means "unset", so a decider's depth 0
        // without zero_conf is a contract error, never a zero-conf acceptance by accident (NL-1181 review)
        if (decision.MinimumDepth is 0 && !decision.ZeroConf)
            return "min_accept_depth 0 without zero_conf: only zero_conf accepts a zero-conf channel";
        var wantsZeroConf = request.ChannelType?.IsFeatureSet(Feature.OptionZeroconf, true) == true;
        var zeroConf = decision.ZeroConf;
        if (wantsZeroConf && !zeroConf)
            return "channel acceptor blocked zero-conf channel negotiation";
        if (zeroConf && !wantsZeroConf)
            return "zero_conf needs the opener's option_zeroconf channel_type: zero-conf channels are not supported "
                 + "otherwise";
        if (zeroConf && decision.MinimumDepth is > 0)
            return "zero_conf with a non-zero min_accept_depth";
        if (decision.ToSelfDelay is 0)
            return "csv_delay must be at least 1";
        if (decision.MaxAcceptedHtlcs is 0 or > MaxAcceptedHtlcsLimit)
            return $"max_htlc_count must be 1 to {MaxAcceptedHtlcsLimit}";
        if (decision.MaxHtlcValueInFlight is { } inFlight && inFlight == LightningMoney.Zero)
            return "in_flight_max_msat must be positive";
        if (decision.HtlcMinimum is { } htlcMinimum && htlcMinimum >= capacity)
            return "min_htlc_in must be below the channel capacity";
        if (decision.UpfrontShutdownScript is { } script && negotiatedFeatures is not null)
        {
            if (negotiatedFeatures.UpfrontShutdownScript == FeatureSupport.No)
                return "requested upfront shutdown to address, but remote peer does not support option upfront "
                     + "shutdown script";
            if (script.Length == 0 || !ShutdownScriptValidator.IsValidUpfront((byte[])script, negotiatedFeatures))
                return "upfront_shutdown is not a valid shutdown script";
        }

        var reserve = local.ChannelReserveAmount;
        if (decision.ChannelReserve is { } wanted)
        {
            if (wanted < request.DustLimit || wanted < local.DustLimitAmount)
                return "reserve lower than proposed dust limit";
            if (wanted >= capacity)
                return "reserve_sat must be below the channel capacity";
            if (request.DualFunded)
            {
                // BOLT 2 fixes the dual-funded reserve: the acceptor's minimum applies when the fixed one meets it
                if (wanted > local.ChannelReserveAmount)
                    return $"reserve_sat {wanted.Satoshi} sat is above the reserve BOLT 2 fixes for this dual-funded "
                         + $"channel ({local.ChannelReserveAmount.Satoshi} sat)";
            }
            else
            {
                reserve = wanted;
            }
        }

        newLocal = new ChannelParty(local.DustLimitAmount, reserve, decision.HtlcMinimum ?? local.HtlcMinimumAmount,
                                    decision.MaxAcceptedHtlcs ?? local.MaxAcceptedHtlcs,
                                    decision.MaxHtlcValueInFlight ?? local.MaxHtlcValueInFlight,
                                    decision.ToSelfDelay ?? local.ToSelfDelay,
                                    decision.UpfrontShutdownScript ?? local.UpfrontShutdownScript);
        newMinimumDepth = zeroConf ? 0U : decision.MinimumDepth ?? minimumDepth;
        return null;
    }

    /// <summary>A rejection text cut to <see cref="MaxErrorLength"/>.</summary>
    public static string Truncate(string? error) =>
        string.IsNullOrEmpty(error) ? ChannelOpenDecision.GenericRejection
        : error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;

    private static bool Conflicts<T>(T? a, T? b) where T : struct => a is { } x && b is { } y && !x.Equals(y);

    private static bool Conflicts(LightningMoney? a, LightningMoney? b) => a is { } x && b is { } y && x != y;
}