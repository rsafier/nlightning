namespace NLightning.Application.Payments.Routing;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Send;

/// <summary>
/// A route <see cref="Interfaces.IRouteQueryService"/> found: the HTLC we would offer and the onion layers after it.
/// </summary>
/// <param name="Route">The route (our peer first, the destination last).</param>
/// <param name="Channel">Our channel of the first HTLC.</param>
/// <param name="Probability">The estimated success probability: the product over the channels after ours of
/// <c>MissionControl</c>'s estimate (the a-priori probability where nothing was learnt; 1 for our own channel).</param>
/// <param name="BlockHeight">The height the CLTVs were computed from.</param>
/// <param name="Description">Which candidate the planner chose (direct, route hint or graph).</param>
/// <param name="TrampolineLayer">The trampoline layer of a quote through a trampoline node (NL-940): what a payment
/// would ask of the node in its trampoline onion, priced with the node's policy the outer route was built
/// around.</param>
public sealed record RouteQuote(
    PaymentRoute Route,
    LocalChannelCandidate Channel,
    double Probability,
    uint BlockHeight,
    string Description,
    TrampolineQuote? TrampolineLayer = null);

/// <summary>
/// The trampoline layer of a <see cref="RouteQuote"/> through a trampoline node (NL-940): the payee's amount and
/// expiry of the trampoline onion, and the node's policy the outer route's amount and expiry were built with
/// (<c>amt_to_forward</c> = the payee's amount + <see cref="Fee"/>, <c>outgoing_cltv_value</c> =
/// <see cref="PayeeCltvExpiry"/> + the policy's delta).
/// </summary>
/// <param name="TrampolineNode">The trampoline node the outer route reaches.</param>
/// <param name="Payee">The payee behind it (the quote's destination).</param>
/// <param name="Amount">What the payee must receive.</param>
/// <param name="PayeeCltvExpiry">The payee's absolute <c>outgoing_cltv_value</c>.</param>
/// <param name="Policy">The node's policy the quote was priced with.</param>
/// <param name="PolicyLearnt">Whether <paramref name="Policy"/> is one the node told us (an earlier
/// <c>trampoline_fee_or_expiry_insufficient</c>), or the send options' default.</param>
/// <param name="Fee">What the node keeps: <paramref name="Policy"/> applied to <paramref name="Amount"/>.</param>
public sealed record TrampolineQuote(
    CompactPubKey TrampolineNode,
    CompactPubKey Payee,
    LightningMoney Amount,
    uint PayeeCltvExpiry,
    TrampolinePolicy Policy,
    bool PolicyLearnt,
    LightningMoney Fee);