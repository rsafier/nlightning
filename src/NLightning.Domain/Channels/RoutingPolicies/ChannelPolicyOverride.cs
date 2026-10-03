namespace NLightning.Domain.Channels.RoutingPolicies;

using ValueObjects;

/// <summary>
/// The routing policy values set for one channel (wave sp1 lane SP1-G, <c>setchannelpolicy</c>): what our
/// <c>channel_update</c> for the channel announces and what our forwarding policy enforces for it. A null value means
/// the node-wide <c>Node:Routing</c> value (<see cref="Node.Options.RoutingOptions"/>).
/// </summary>
/// <remarks>
/// The field widths are BOLT 7 <c>channel_update</c>'s: <c>u32 fee_base_msat</c>, <c>u32
/// fee_proportional_millionths</c>, <c>u16 cltv_expiry_delta</c>, <c>u64 htlc_minimum_msat</c>, <c>u64
/// htlc_maximum_msat</c>. Persisted by lane SP1-C (migration owner) through
/// <see cref="Interfaces.IChannelPolicyDbRepository"/>; as a patch (<see cref="IChannelPolicyService.SetAsync"/>) a null
/// value leaves the stored one unchanged.
/// </remarks>
/// <param name="ChannelId">The channel.</param>
/// <param name="FeeBaseMsat">Base fee per forwarded HTLC.</param>
/// <param name="FeeProportionalMillionths">Proportional fee.</param>
/// <param name="CltvExpiryDelta">CLTV delta (at least <see cref="Node.Options.RoutingOptions.MinimumCltvExpiryDelta"/>).</param>
/// <param name="HtlcMinimumMsat">Smallest HTLC we forward on the channel.</param>
/// <param name="HtlcMaximumMsat">Largest HTLC we forward on the channel (at most the capacity).</param>
/// <param name="UpdatedAt">When the override was last written.</param>
public sealed record ChannelPolicyOverride(
    ChannelId ChannelId,
    uint? FeeBaseMsat = null,
    uint? FeeProportionalMillionths = null,
    ushort? CltvExpiryDelta = null,
    ulong? HtlcMinimumMsat = null,
    ulong? HtlcMaximumMsat = null,
    DateTimeOffset UpdatedAt = default);