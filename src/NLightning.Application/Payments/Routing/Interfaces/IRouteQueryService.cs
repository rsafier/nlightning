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
}