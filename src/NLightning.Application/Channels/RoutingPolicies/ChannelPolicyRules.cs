namespace NLightning.Application.Channels.RoutingPolicies;

using Domain.Channels.Models;
using Domain.Channels.RoutingPolicies;
using Domain.Node.Options;

/// <summary>
/// The pure rules of per-channel routing policies (wave sp1 lane SP1-G): what a channel announces in its
/// <c>channel_update</c> and which override values are acceptable (BOLT 7 "The channel_update Message").
/// </summary>
public static class ChannelPolicyRules
{
    /// <summary>
    /// The policy in force for <paramref name="channel"/>: fee, CLTV delta and configured HTLC range from
    /// <see cref="ConfiguredChannelPolicy.From"/>, then the HTLC range the channel can carry:
    /// <c>htlc_minimum_msat</c> = the larger of the peer's <c>htlc_minimum_msat</c> and the configured minimum,
    /// <c>htlc_maximum_msat</c> = the smallest of the capacity (BOLT 7: MUST NOT exceed it), the peer's
    /// <c>max_htlc_value_in_flight_msat</c> (when set) and the configured maximum.
    /// </summary>
    /// <remarks>A channel without a funding output counts as a zero capacity (its maximum is 0).</remarks>
    public static EffectiveChannelPolicy Resolve(ChannelModel channel, RoutingOptions routing,
                                                 ChannelPolicyOverride? policyOverride)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var configured = ConfiguredChannelPolicy.From(routing, policyOverride);
        var (minimum, maximum) = GetHtlcRange(channel, configured);
        return new EffectiveChannelPolicy(channel.ChannelId, configured.FeeBaseMsat,
                                          configured.FeeProportionalMillionths, configured.CltvExpiryDelta, minimum,
                                          maximum, policyOverride);
    }

    /// <summary>
    /// The HTLC range <paramref name="channel"/> announces under <paramref name="configured"/> (rules on
    /// <see cref="Resolve"/>).
    /// </summary>
    public static (ulong Minimum, ulong Maximum) GetHtlcRange(ChannelModel channel, ConfiguredChannelPolicy configured)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(configured);

        // A default ChannelParams (a channel row read before its parameters were known) has no amounts
        var remote = channel.ChannelParams.Remote;
        var minimum = Math.Max(AmountOrZero(remote.HtlcMinimumAmount), configured.HtlcMinimumMsat);
        var maximum = GetCapacityMsat(channel);
        var remoteMaxInFlight = AmountOrZero(remote.MaxHtlcValueInFlight);
        if (remoteMaxInFlight > 0)
            maximum = Math.Min(maximum, remoteMaxInFlight);
        if (configured.HtlcMaximumMsat is { } configuredMaximum)
            maximum = Math.Min(maximum, configuredMaximum);

        return (minimum, maximum);
    }

    /// <summary>
    /// Applies <paramref name="patch"/> to <paramref name="current"/>: every non-null value of the patch replaces the
    /// stored one, every null keeps it.
    /// </summary>
    public static ChannelPolicyOverride Merge(ChannelPolicyOverride? current, ChannelPolicyOverride patch,
                                              DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return new ChannelPolicyOverride(patch.ChannelId, patch.FeeBaseMsat ?? current?.FeeBaseMsat,
                                         patch.FeeProportionalMillionths ?? current?.FeeProportionalMillionths,
                                         patch.CltvExpiryDelta ?? current?.CltvExpiryDelta,
                                         patch.HtlcMinimumMsat ?? current?.HtlcMinimumMsat,
                                         patch.HtlcMaximumMsat ?? current?.HtlcMaximumMsat, updatedAt);
    }

    /// <summary>Whether <paramref name="policyOverride"/> sets no value at all.</summary>
    public static bool IsEmpty(ChannelPolicyOverride? policyOverride) =>
        policyOverride is null
     || (policyOverride.FeeBaseMsat is null && policyOverride.FeeProportionalMillionths is null
      && policyOverride.CltvExpiryDelta is null && policyOverride.HtlcMinimumMsat is null
      && policyOverride.HtlcMaximumMsat is null);

    /// <summary>Whether both overrides set the same values (the channel and <c>UpdatedAt</c> are not compared).</summary>
    public static bool HasSameValues(ChannelPolicyOverride? left, ChannelPolicyOverride? right)
    {
        if (IsEmpty(left) || IsEmpty(right))
            return IsEmpty(left) && IsEmpty(right);

        return left!.FeeBaseMsat == right!.FeeBaseMsat
            && left.FeeProportionalMillionths == right.FeeProportionalMillionths
            && left.CltvExpiryDelta == right.CltvExpiryDelta && left.HtlcMinimumMsat == right.HtlcMinimumMsat
            && left.HtlcMaximumMsat == right.HtlcMaximumMsat;
    }

    /// <summary>
    /// Every reason <paramref name="policyOverride"/> cannot be the policy of <paramref name="channel"/>; empty when it
    /// can.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><c>cltv_expiry_delta</c> at least <see cref="RoutingOptions.MinimumCltvExpiryDelta"/> (BOLT 2 "Risks With
    ///   HTLC Timeouts"), above <see cref="RoutingOptions.ExpiryTooSoonBlocks"/> and at most
    ///   <see cref="RoutingOptions.MaxCltvExpiryDistance"/> (the node-wide rules of
    ///   <see cref="RoutingOptions.GetValidationErrors"/>).</item>
    ///   <item><c>htlc_maximum_msat</c> positive and at most the channel capacity (BOLT 7: MUST NOT exceed it).</item>
    ///   <item>the announced <c>htlc_minimum_msat</c> (the larger of the peer's minimum and ours) at most the announced
    ///   <c>htlc_maximum_msat</c>: otherwise no HTLC fits and we could send no update at all.</item>
    /// </list>
    /// The fee fields are <c>u32</c> as in BOLT 7, so every value the type holds can be announced.
    /// </remarks>
    public static IReadOnlyList<string> GetValidationErrors(ChannelModel channel, RoutingOptions routing,
                                                            ChannelPolicyOverride policyOverride)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(policyOverride);

        var errors = new List<string>();
        if (policyOverride.ChannelId != channel.ChannelId)
            errors.Add($"The policy names channel {policyOverride.ChannelId}, not {channel.ChannelId}.");

        if (policyOverride.CltvExpiryDelta is { } cltvExpiryDelta)
        {
            if (cltvExpiryDelta < RoutingOptions.MinimumCltvExpiryDelta)
                errors.Add($"cltv_expiry_delta {cltvExpiryDelta} is below {RoutingOptions.MinimumCltvExpiryDelta} "
                         + "blocks (BOLT 2 \"Risks With HTLC Timeouts\").");
            if (cltvExpiryDelta <= routing.ExpiryTooSoonBlocks)
                errors.Add($"cltv_expiry_delta {cltvExpiryDelta} must be above Routing:ExpiryTooSoonBlocks "
                         + $"({routing.ExpiryTooSoonBlocks}).");
            if (cltvExpiryDelta > routing.MaxCltvExpiryDistance)
                errors.Add($"cltv_expiry_delta {cltvExpiryDelta} is above Routing:MaxCltvExpiryDistance "
                         + $"({routing.MaxCltvExpiryDistance}).");
        }

        var capacityMsat = GetCapacityMsat(channel);
        if (policyOverride.HtlcMaximumMsat is { } htlcMaximumMsat)
        {
            if (htlcMaximumMsat == 0)
                errors.Add("htlc_maximum_msat must be positive.");
            else if (htlcMaximumMsat > capacityMsat)
                errors.Add($"htlc_maximum_msat {htlcMaximumMsat} is above the channel capacity ({capacityMsat} msat; "
                         + "BOLT 7: MUST NOT exceed it).");
        }

        if (errors.Count == 0)
        {
            var (minimum, maximum) = GetHtlcRange(channel, ConfiguredChannelPolicy.From(routing, policyOverride));
            if (minimum > maximum)
                errors.Add($"htlc_minimum_msat {minimum} (the larger of ours and the peer's) is above "
                         + $"htlc_maximum_msat {maximum} (the smallest of ours, the peer's max_htlc_value_in_flight_msat "
                         + "and the capacity): no HTLC would fit.");
        }

        return errors;
    }

    private static ulong GetCapacityMsat(ChannelModel channel) => channel.FundingOutput?.Amount.MilliSatoshi ?? 0;

    private static ulong AmountOrZero(Domain.Money.LightningMoney? amount) => amount?.MilliSatoshi ?? 0;
}