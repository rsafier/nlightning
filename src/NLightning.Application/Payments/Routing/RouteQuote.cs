namespace NLightning.Application.Payments.Routing;

/// <summary>
/// A route <see cref="Interfaces.IRouteQueryService"/> found: the HTLC we would offer and the onion layers after it.
/// </summary>
/// <param name="Route">The route (our peer first, the destination last).</param>
/// <param name="Channel">Our channel of the first HTLC.</param>
/// <param name="Probability">The estimated success probability: the product over the channels after ours of
/// <c>MissionControl</c>'s estimate (the a-priori probability where nothing was learnt; 1 for our own channel).</param>
/// <param name="BlockHeight">The height the CLTVs were computed from.</param>
/// <param name="Description">Which candidate the planner chose (direct, route hint or graph).</param>
public sealed record RouteQuote(
    PaymentRoute Route,
    LocalChannelCandidate Channel,
    double Probability,
    uint BlockHeight,
    string Description);