namespace NLightning.Application.Channels.RoutingPolicies;

using Domain.Channels.RoutingPolicies;
using Domain.Node.Options;

/// <summary>
/// The routing values configured for one channel (wave sp1 lane SP1-G): its <see cref="ChannelPolicyOverride"/> value
/// where set, the node-wide <c>Node:Routing</c> value (<see cref="RoutingOptions"/>) elsewhere. The channel's own limits
/// (the peer's <c>htlc_minimum_msat</c> and <c>max_htlc_value_in_flight_msat</c>, the capacity) are not applied here:
/// see <see cref="ChannelPolicyRules.Resolve"/> for what the channel announces.
/// </summary>
/// <param name="FeeBaseMsat">BOLT 7 <c>fee_base_msat</c>.</param>
/// <param name="FeeProportionalMillionths">BOLT 7 <c>fee_proportional_millionths</c>.</param>
/// <param name="CltvExpiryDelta">BOLT 7 <c>cltv_expiry_delta</c>.</param>
/// <param name="HtlcMinimumMsat">Our smallest forwarded HTLC on the channel.</param>
/// <param name="HtlcMaximumMsat">Our largest forwarded HTLC on the channel, or null for "the channel's limits only".
/// </param>
public sealed record ConfiguredChannelPolicy(
    uint FeeBaseMsat,
    uint FeeProportionalMillionths,
    ushort CltvExpiryDelta,
    ulong HtlcMinimumMsat,
    ulong? HtlcMaximumMsat)
{
    /// <summary>
    /// The values of <paramref name="policyOverride"/> where set, else those of <paramref name="routing"/>.
    /// </summary>
    public static ConfiguredChannelPolicy From(RoutingOptions routing, ChannelPolicyOverride? policyOverride)
    {
        ArgumentNullException.ThrowIfNull(routing);
        return new ConfiguredChannelPolicy(policyOverride?.FeeBaseMsat ?? routing.FeeBaseMsat,
                                           policyOverride?.FeeProportionalMillionths
                                        ?? routing.FeeProportionalMillionths,
                                           policyOverride?.CltvExpiryDelta ?? routing.CltvExpiryDelta,
                                           policyOverride?.HtlcMinimumMsat ?? routing.HtlcMinimumMsat,
                                           policyOverride?.HtlcMaximumMsat ?? routing.HtlcMaximumMsat);
    }
}