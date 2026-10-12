namespace NLightning.Domain.Channels.RoutingPolicies;

using ValueObjects;

/// <summary>
/// The routing policy in force for one channel: the channel's <see cref="ChannelPolicyOverride"/> values where set,
/// the node-wide <c>Node:Routing</c> values (and the channel's limits for the HTLC range) elsewhere (lane SP1-G).
/// </summary>
/// <param name="ChannelId">The channel.</param>
/// <param name="FeeBaseMsat">Base fee per forwarded HTLC.</param>
/// <param name="FeeProportionalMillionths">Proportional fee.</param>
/// <param name="CltvExpiryDelta">CLTV delta.</param>
/// <param name="HtlcMinimumMsat">Smallest HTLC forwarded.</param>
/// <param name="HtlcMaximumMsat">Largest HTLC forwarded.</param>
/// <param name="Override">The stored override, or null when the channel has none.</param>
public sealed record EffectiveChannelPolicy(
    ChannelId ChannelId,
    uint FeeBaseMsat,
    uint FeeProportionalMillionths,
    ushort CltvExpiryDelta,
    ulong HtlcMinimumMsat,
    ulong HtlcMaximumMsat,
    ChannelPolicyOverride? Override = null)
{
    /// <summary><see cref="FeeBaseMsat"/> comes from the override.</summary>
    public bool IsFeeBaseMsatOverridden => Override?.FeeBaseMsat is not null;

    /// <summary><see cref="FeeProportionalMillionths"/> comes from the override.</summary>
    public bool IsFeeProportionalMillionthsOverridden => Override?.FeeProportionalMillionths is not null;

    /// <summary><see cref="CltvExpiryDelta"/> comes from the override.</summary>
    public bool IsCltvExpiryDeltaOverridden => Override?.CltvExpiryDelta is not null;

    /// <summary><see cref="HtlcMinimumMsat"/> comes from the override.</summary>
    public bool IsHtlcMinimumMsatOverridden => Override?.HtlcMinimumMsat is not null;

    /// <summary><see cref="HtlcMaximumMsat"/> comes from the override.</summary>
    public bool IsHtlcMaximumMsatOverridden => Override?.HtlcMaximumMsat is not null;
}