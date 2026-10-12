namespace NLightning.Application.Payments.Routing.Interfaces;

using Domain.Crypto.ValueObjects;
using Domain.Money;

/// <summary>
/// Answers <c>getroute</c> (<c>ClientCommand.GetRoute</c>, BOLT 7 plan G4-T4): the route a payment of an amount to a
/// node would take now, planned exactly as a payment's first round (our direct channels, then the graph), without
/// sending anything.
/// </summary>
public interface IRouteQueryService
{
    /// <summary>
    /// The route that would deliver <paramref name="amount"/> to <paramref name="payee"/> in one HTLC.
    /// </summary>
    /// <param name="payee">The destination node.</param>
    /// <param name="amount">What the destination must receive.</param>
    /// <param name="maxFee">The most the route may cost in fees; null uses the payment default
    /// (<c>PaymentSendOptions.GetMaxFee</c>).</param>
    /// <param name="finalCltvDelta">The destination's <c>min_final_cltv_expiry_delta</c>; null uses BOLT 11's
    /// default of 18.</param>
    /// <param name="trampolineNode">Quote the outer route to a trampoline node instead (NL-940): the route is what a
    /// payment through it would send in its first round, priced with the node's cached (or the default) policy, and
    /// the quote's <see cref="RouteQuote.TrampolineLayer"/> carries that layer.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <exception cref="ArgumentException">A zero amount, the destination is us, or the trampoline node is us or the
    /// destination.</exception>
    /// <exception cref="InvalidOperationException">No block was processed yet, or no route fits (the message says
    /// why).</exception>
    Task<RouteQuote> QuoteRouteAsync(CompactPubKey payee, LightningMoney amount, LightningMoney? maxFee,
                                     ushort? finalCltvDelta, CompactPubKey? trampolineNode = null,
                                     CancellationToken cancellationToken = default);

    /// <summary>
    /// LND <c>QueryRoutes</c> (NL-1242): the route a one-part payment of <see cref="RouteQueryRequest.Amount"/> to
    /// <see cref="RouteQueryRequest.Payee"/> would take now under the query's restrictions (fee and CLTV limits,
    /// ignored nodes and directed pairs, allowed first-hop channels, a fixed last hop, route hints, mission control
    /// on or off), the destination's expiry exactly the height plus <see cref="RouteQueryRequest.FinalCltvDelta"/>.
    /// The payee may be this node (a circular route back over one of our channels).
    /// </summary>
    /// <exception cref="ArgumentException">A zero amount or an impossible last hop.</exception>
    /// <exception cref="InvalidOperationException">No block was processed yet, or no route fits (the message says
    /// why).</exception>
    Task<RouteQuote> QueryRouteAsync(RouteQueryRequest query, CancellationToken cancellationToken = default);
}