namespace NLightning.Domain.Client.Requests;

using Crypto.ValueObjects;

/// <summary>
/// Pays over exactly the routes the caller supplied (<c>ClientCommand.PayRoute</c>, NL-1082): they are offered as
/// given, never re-planned, and every route's outcome is reported. The identity is either a BOLT 11 invoice (hash,
/// secret, amount and <c>basic_mpp</c> support derive from it) or a raw payment hash with an optional secret and an
/// explicit total (the LND <c>SendToRoute</c> form).
/// </summary>
public sealed class PayRouteClientRequest
{
    /// <summary>
    /// The BOLT 11 invoice being paid; exclusive with <see cref="PaymentHash"/>.
    /// </summary>
    public string? Bolt11 { get; init; }

    /// <summary>
    /// A raw payment hash; exclusive with <see cref="Bolt11"/>.
    /// </summary>
    public Hash? PaymentHash { get; init; }

    /// <summary>
    /// The payment secret of the raw form (the invoice form takes it from the invoice); null for none. With null the
    /// final hop carries the all-zero secret — modern receivers require a real one and fail the payment back.
    /// </summary>
    public Secret? PaymentSecret { get; init; }

    /// <summary>
    /// The payment's <c>total_msat</c> every shard reports, in msat. Null: the invoice amount, or (raw form, one
    /// route) the route's delivered amount. Required for a raw multi-route set.
    /// </summary>
    public ulong? TotalMsatMsat { get; init; }

    /// <summary>
    /// The routes to offer, our peer first on each; 1 to 128 of them.
    /// </summary>
    public required IReadOnlyList<PayRouteRouteClientInfo> Routes { get; init; }

    /// <summary>
    /// How long to wait for the outcome, in seconds (default 60, at most 300). The routes keep resolving after the
    /// wait ends; the response then reports them still in flight.
    /// </summary>
    public uint TimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// The most the payment may pay in routing fees, in msat, or null for the node's default (NL-270).
    /// </summary>
    public ulong? MaxFeeMsat { get; init; }

    /// <summary>
    /// The operator's label (NL-602 A3-T1, <c>--label</c>): stored on the row and copied into the accounting event's
    /// details; null for none. Checked by the daemon (<c>SourceLabelRules</c>).
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// The operator's tags as <c>key=value</c> (NL-602 A3-T1, <c>--tag</c>, repeatable); null for none. Checked by the
    /// daemon (<c>SourceLabelRules</c>).
    /// </summary>
    public IReadOnlyList<string>? Tags { get; init; }
}

/// <summary>
/// One caller-supplied route of a <see cref="PayRouteClientRequest"/>: the channel of ours the first HTLC leaves
/// through, what that HTLC carries (its amount and absolute <c>cltv_expiry</c>), and the hops after ours.
/// </summary>
/// <param name="FirstHopChannel">Our channel of the first HTLC, as a channel id (64 hex characters) or a short channel
/// id (<c>BLOCKxTXxOUTPUT</c>) of one of our channels.</param>
/// <param name="FirstHopAmountMsat">What our first HTLC carries, in msat.</param>
/// <param name="FirstHopCltv">Our first HTLC's <c>cltv_expiry</c>.</param>
/// <param name="Hops">The hops after ours (our peer first, the payee last).</param>
public sealed record PayRouteRouteClientInfo(
    string FirstHopChannel,
    ulong FirstHopAmountMsat,
    uint FirstHopCltv,
    IReadOnlyList<PayRouteHopClientInfo> Hops);

/// <summary>
/// One hop of a <see cref="PayRouteRouteClientInfo"/>: the node, the channel it forwards over, the amount it
/// forwards onward and the absolute <c>outgoing_cltv_value</c> of that HTLC — the same shape <c>getroute</c> reports,
/// so its answer can be edited and paid back.
/// </summary>
/// <param name="NodeId">The node.</param>
/// <param name="OutgoingShortChannelId">The channel it forwards over (the 8-byte short channel id as a number);
/// null on the payee's final hop.</param>
/// <param name="AmountToForwardMsat">What the hop forwards onward, in msat.</param>
/// <param name="OutgoingCltvValue">The HTLC it forwards' absolute <c>outgoing_cltv_value</c>.</param>
public sealed record PayRouteHopClientInfo(
    CompactPubKey NodeId,
    ulong? OutgoingShortChannelId,
    ulong AmountToForwardMsat,
    uint OutgoingCltvValue);