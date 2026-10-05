namespace NLightning.Domain.Payments.Models;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Money;

/// <summary>
/// One hop of a caller-supplied route (<c>payroute</c>, NL-1145): the node, the channel it forwards over
/// (<see cref="OutgoingShortChannelId"/>, null on the payee's final hop), the amount it forwards onward and the
/// absolute <c>outgoing_cltv_value</c> of that HTLC — the same shape <c>getroute</c> reports, so its answer can be
/// edited and paid back.
/// </summary>
public sealed record PayRouteHop(CompactPubKey NodeId, ShortChannelId? OutgoingShortChannelId,
                                 LightningMoney AmountToForward, uint OutgoingCltvValue);

/// <summary>
/// One caller-supplied route: the channel of ours the first HTLC leaves through, what that HTLC carries (its amount
/// and absolute <c>cltv_expiry</c>), and the hops after ours (our peer first, the payee last).
/// </summary>
public sealed record PayRouteRoute(ChannelId FirstHopChannelId, LightningMoney FirstHopAmount,
                                   uint FirstHopCltvExpiry, IReadOnlyList<PayRouteHop> Hops);

/// <summary>
/// A payment sent over exactly the routes the caller supplied (<c>payroute</c>, NL-1145): the daemon offers them as
/// given, never re-plans, and reports each route's outcome. The identity is either a BOLT 11 invoice (hash, secret,
/// amount and <c>basic_mpp</c> support derive from it) or a raw payment hash with an optional secret and an explicit
/// total (the LND <c>SendToRoute</c> form).
/// </summary>
public sealed class PayRouteRequest
{
    /// <summary>The BOLT 11 invoice being paid; exclusive with <see cref="PaymentHash"/>.</summary>
    public string? Bolt11 { get; init; }

    /// <summary>A raw payment hash; exclusive with <see cref="Bolt11"/>.</summary>
    public Hash? PaymentHash { get; init; }

    /// <summary>
    /// The payment secret of the raw form (the invoice form takes it from the invoice). Null: the final hop carries
    /// the all-zero secret — modern receivers require a real one and fail the payment back.
    /// </summary>
    public Secret? PaymentSecret { get; init; }

    /// <summary>
    /// The payment's <c>total_msat</c> every shard reports. Default: the invoice amount, or (raw form, one route)
    /// the route's delivered amount. Required for a raw multi-route set.
    /// </summary>
    public LightningMoney? TotalAmount { get; init; }

    /// <summary>The routes to offer, our peer first on each; 1 to 128 of them.</summary>
    public required IReadOnlyList<PayRouteRoute> Routes { get; init; }
}