namespace NLightning.Domain.Client.Responses;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Money;

/// <summary>
/// The route a payment would take (<c>ClientCommand.GetRoute</c>): our HTLC, then one hop per node it reaches.
/// </summary>
/// <param name="ChannelId">Our channel of the first HTLC.</param>
/// <param name="Hops">Our peer first, the destination last.</param>
/// <param name="Amount">What our first HTLC carries (the destination's amount plus every fee).</param>
/// <param name="Fee">The fees of the whole route.</param>
/// <param name="CltvExpiry">Our first HTLC's <c>cltv_expiry</c>.</param>
/// <param name="BlockHeight">The height the CLTVs were computed from.</param>
/// <param name="Probability">The estimated success probability.</param>
/// <param name="Description">Which candidate was chosen (direct, route hint or graph).</param>
public sealed record GetRouteClientResponse(
    ChannelId ChannelId,
    IReadOnlyList<GetRouteHop> Hops,
    LightningMoney Amount,
    LightningMoney Fee,
    uint CltvExpiry,
    uint BlockHeight,
    double Probability,
    string Description);

/// <summary>
/// One node of a <see cref="GetRouteClientResponse"/> and the HTLC it receives.
/// </summary>
/// <param name="NodeId">The node.</param>
/// <param name="ShortChannelId">The channel its HTLC arrives on.</param>
/// <param name="Amount">What the HTLC carries.</param>
/// <param name="CltvExpiry">The HTLC's <c>cltv_expiry</c>.</param>
/// <param name="Fee">What the node keeps for forwarding (zero for the destination).</param>
public sealed record GetRouteHop(
    CompactPubKey NodeId,
    ShortChannelId ShortChannelId,
    LightningMoney Amount,
    uint CltvExpiry,
    LightningMoney Fee);