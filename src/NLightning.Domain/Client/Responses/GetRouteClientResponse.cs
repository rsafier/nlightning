namespace NLightning.Domain.Client.Responses;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Money;

/// <summary>
/// The route a payment would take (<c>ClientCommand.GetRoute</c>): our HTLC, then one hop per node it reaches.
/// </summary>
/// <param name="ChannelId">Our channel of the first HTLC.</param>
/// <param name="Hops">Our peer first, the destination last (the trampoline node last, keeping its trampoline fee,
/// when <paramref name="Trampoline"/> is set).</param>
/// <param name="Amount">What our first HTLC carries (the destination's amount plus every fee).</param>
/// <param name="Fee">The fees of the whole route (the trampoline node's included).</param>
/// <param name="CltvExpiry">Our first HTLC's <c>cltv_expiry</c>.</param>
/// <param name="BlockHeight">The height the CLTVs were computed from.</param>
/// <param name="Probability">The estimated success probability.</param>
/// <param name="Description">Which candidate was chosen (direct, route hint or graph).</param>
/// <param name="Trampoline">The trampoline layer of a quote through a trampoline node (NL-940): the payee it would
/// reach, at what amount and expiry, and the policy the outer route was priced with; null for our own route.</param>
public sealed record GetRouteClientResponse(
    ChannelId ChannelId,
    IReadOnlyList<GetRouteHop> Hops,
    LightningMoney Amount,
    LightningMoney Fee,
    uint CltvExpiry,
    uint BlockHeight,
    double Probability,
    string Description,
    GetRouteTrampoline? Trampoline = null);

/// <summary>
/// One hop of a <see cref="GetRouteClientResponse"/> and the HTLC it receives.
/// </summary>
/// <param name="NodeId">The node.</param>
/// <param name="ShortChannelId">The channel its HTLC arrives on.</param>
/// <param name="Amount">What the HTLC carries.</param>
/// <param name="CltvExpiry">The HTLC's <c>cltv_expiry</c>.</param>
/// <param name="Fee">What the node keeps for forwarding (zero for the destination; the trampoline node's fee when it
/// is the last hop of a trampoline quote).</param>
public sealed record GetRouteHop(
    CompactPubKey NodeId,
    ShortChannelId ShortChannelId,
    LightningMoney Amount,
    uint CltvExpiry,
    LightningMoney Fee);

/// <summary>
/// The trampoline layer of a <see cref="GetRouteClientResponse"/> (NL-940): what a payment through the trampoline
/// node would ask of it in its trampoline onion, and the policy the quote was priced with.
/// </summary>
/// <param name="TrampolineNode">The trampoline node.</param>
/// <param name="Payee">The payee behind it (the request's destination).</param>
/// <param name="Amount">What the payee must receive.</param>
/// <param name="PayeeCltvExpiry">The payee's absolute <c>outgoing_cltv_value</c>.</param>
/// <param name="FeeBaseMsat">The policy's <c>fee_base_msat</c>.</param>
/// <param name="FeeProportionalMillionths">The policy's <c>fee_proportional_millionths</c>.</param>
/// <param name="CltvExpiryDelta">The policy's <c>cltv_expiry_delta</c>.</param>
/// <param name="Fee">What the node keeps: the policy applied to <paramref name="Amount"/>.</param>
/// <param name="PolicyLearnt">Whether the policy is one the node told us (an earlier
/// <c>trampoline_fee_or_expiry_insufficient</c>), or the send options' default.</param>
public sealed record GetRouteTrampoline(
    CompactPubKey TrampolineNode,
    CompactPubKey Payee,
    LightningMoney Amount,
    uint PayeeCltvExpiry,
    uint FeeBaseMsat,
    uint FeeProportionalMillionths,
    ushort CltvExpiryDelta,
    LightningMoney Fee,
    bool PolicyLearnt);