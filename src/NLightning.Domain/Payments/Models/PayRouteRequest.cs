namespace NLightning.Domain.Payments.Models;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Money;

/// <summary>
/// One hop of a caller-supplied route (<c>payroute</c>, NL-1082): the node, the channel it forwards over
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
/// A payment sent over exactly the routes the caller supplied (<c>payroute</c>, NL-1082): the daemon offers them as
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

    /// <summary>
    /// A keysend payment over the supplied route (LND <c>SendToRouteV2</c> with a <c>keysend_preimage</c> record,
    /// NL-1242): the payee's payload carries this preimage and <see cref="CustomRecords"/> instead of
    /// <c>payment_data</c>. Raw form only, one route, and <see cref="PaymentHash"/> must be its SHA256.
    /// </summary>
    public Secret? KeysendPreimage { get; init; }

    /// <summary>The payee's application records of a keysend payment (types of 65536 or more, not the keysend
    /// preimage); none otherwise.</summary>
    public IReadOnlyList<Keysend.CustomRecord> CustomRecords { get; init; } = [];

    /// <summary>The routes to offer, our peer first on each; 1 to 128 of them.</summary>
    public required IReadOnlyList<PayRouteRoute> Routes { get; init; }

    /// <summary>
    /// How the call relates to a <c>payroute</c> payment of the same hash still in flight (phase C, NL-1276):
    /// <see cref="PayRouteAttachMode.Never"/> (default) starts a payment and is refused while one is in flight,
    /// <see cref="PayRouteAttachMode.Required"/> adds the routes to the in-flight one (<c>payroute --attach</c>) and
    /// <see cref="PayRouteAttachMode.IfInFlight"/> does either (LND <c>SendToRouteV2</c>).
    /// </summary>
    public PayRouteAttachMode Attach { get; init; }

    /// <summary>
    /// The call is one shard attempt of a set the caller assembles over several calls (LND <c>SendToRouteV2</c>,
    /// NL-1276): its routes may deliver less than the total, the parts in flight may never deliver more than it
    /// together, and the call answers once its own routes are resolved instead of when the payment is.
    /// </summary>
    public bool IndependentShards { get; init; }

    /// <summary>
    /// For an <see cref="IndependentShards"/> call (LND <c>SendToRouteV2</c>'s <c>skip_temp_err</c>, NL-1276): a
    /// temporary failure of the call's routes leaves the payment open for more shards. False (LND's default): any
    /// failure of the call's routes, an offer our channel refuses included, fails the payment pending: no more routes
    /// may attach while its other parts are in flight, and it fails once they are resolved (LND
    /// <c>ErrPaymentPendingFailed</c>). A failure the node would never retry (the payee's permanent failure, an
    /// unreadable error) fails the payment pending whatever the flag. Ignored for other calls: the routes of
    /// <c>payroute</c> itself never end the set on a temporary failure; replacing them is what
    /// <see cref="PayRouteAttachMode.Required"/> is for.
    /// </summary>
    public bool SkipTemporaryFailures { get; init; }
}

/// <summary>
/// How a <c>payroute</c> call relates to a <c>payroute</c> payment of the same hash still in flight (NL-1276).
/// </summary>
public enum PayRouteAttachMode
{
    /// <summary>Start a payment; refused while one of the hash is in flight.</summary>
    Never = 0,

    /// <summary>Add the routes to the in-flight <c>payroute</c> payment of the hash; refused when there is none.</summary>
    Required = 1,

    /// <summary>Add the routes to the in-flight <c>payroute</c> payment of the hash when there is one, else start
    /// one.</summary>
    IfInFlight = 2
}