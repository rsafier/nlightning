namespace NLightning.Domain.Payments.Interfaces;

using Crypto.ValueObjects;
using Keysend;
using Models;
using Money;

/// <summary>
/// Sends our payments (BOLT2 plan N8-T3, ONION M4-T6 through route hints; implemented by <c>PaymentService</c>).
/// </summary>
/// <remarks>
/// <para><see cref="PayInvoiceAsync"/>: decode and validate the BOLT 11 invoice (network, expiry, signature, features);
/// pick the route (the payee directly when it is our peer with a usable channel, else our channel to the first
/// route-hint node followed by the hint hops, with the hints' fees and CLTV deltas); build the onion with a CSPRNG
/// session key (final <c>cltv_expiry</c> = height + <c>c</c> + 3); <b>persist</b> the <see cref="PaymentModel"/> as
/// <c>InFlight</c>; then offer the HTLC through <c>IChannelOperations.OfferHtlcAsync</c> with
/// <c>HtlcOrigin.Local(paymentHash)</c> and record its id.</para>
/// <para>The payment completes when the channel layer raises <c>OutgoingHtlcFulfilled</c> (succeeded, with the
/// preimage) or <c>OutgoingHtlcFailed</c> (failed only once the removal is irrevocable; the error onion is decrypted
/// with the per-hop shared secrets and interpreted with <c>FailureInterpreter</c>). A second payment for a hash that
/// is in flight or succeeded is refused; a hash whose stored payment failed may be paid again, and the new attempt
/// replaces the failed one (<c>IPaymentDbRepository.AddAsync</c>). Within one call, a retryable failure is retried
/// automatically and a payment may be split (see the <see cref="PayInvoiceOptions"/> overload).</para>
/// </remarks>
public interface IPaymentService
{
    /// <summary>
    /// Pays a BOLT 11 invoice and waits for the outcome.
    /// </summary>
    /// <param name="bolt11">The invoice.</param>
    /// <param name="amount">The amount for an invoice without one; must be null (or equal) when the invoice sets it.</param>
    /// <param name="timeout">How long to wait for the outcome. On timeout the returned payment is still
    /// <c>InFlight</c> (the HTLC stays offered and resolves later); it is never failed locally while the HTLC is
    /// pending.</param>
    /// <param name="cancellationToken">Stops waiting, like <paramref name="timeout"/>.</param>
    /// <returns>The payment as stored when it completed or the wait ended.</returns>
    /// <exception cref="ArgumentException">The invoice is malformed, expired, for another network, or the amount is
    /// missing or inconsistent. Nothing is persisted.</exception>
    /// <exception cref="InvalidOperationException">A payment for the invoice's hash is already in flight or
    /// succeeded. Nothing is persisted.</exception>
    Task<PaymentModel> PayInvoiceAsync(string bolt11, LightningMoney? amount, TimeSpan timeout,
                                       CancellationToken cancellationToken = default);

    /// <summary>
    /// Pays a BOLT 11 invoice within per-call limits (NL-270), retrying and splitting the payment as needed, and
    /// waits for the outcome.
    /// </summary>
    /// <remarks>
    /// <para>Retries: a failure that another route or a corrected one can get past (a hop's
    /// <c>temporary_channel_failure</c>, an UPDATE failure whose signed <c>channel_update</c> gives the hop's new fee,
    /// CLTV delta or minimum, <c>expiry_too_soon</c>, a node failure of an intermediate hop, the payee's
    /// <c>mpp_timeout</c>) sends the amount again over what is left, within the fee limit, the part limit, the node's
    /// attempt budget and the timeout. A permanent failure (the payee's PERM failures such as
    /// <c>incorrect_or_unknown_payment_details</c>) or a local error stops the payment.</para>
    /// <para>Split (BOLT 4 <c>basic_mpp</c>, only when the invoice offers it): when no single route can carry the
    /// amount, it is sent as several HTLCs with the same payment hash, each with <c>payment_secret</c> and
    /// <c>total_msat</c> = the amount, over our direct channels to the payee and the invoice's route hints.</para>
    /// <para>Our own invoice (NL-609, a circular rebalance): paid over a circular route, out through one of our channels
    /// and back in through another (never the same one), the incoming channel's last hop priced with the peer's
    /// <c>channel_update</c>; refused when the invoice is not <c>Open</c> or no such route exists.
    /// <see cref="PayInvoiceOptions.OutgoingChannelId"/> and <see cref="PayInvoiceOptions.IncomingChannelId"/> pin the
    /// two channels.</para>
    /// </remarks>
    /// <param name="bolt11">The invoice.</param>
    /// <param name="amount">The amount for an invoice without one; must be null (or equal) when the invoice sets it.</param>
    /// <param name="options">The per-call fee limit, part limit, timeout and channel pins.</param>
    /// <param name="cancellationToken">Stops waiting and retrying, like the timeout.</param>
    /// <exception cref="ArgumentException">The invoice is malformed, expired, for another network, the amount is
    /// missing or inconsistent, an option is out of range, or a pin cannot be used (an incoming pin for another node's
    /// invoice, the same channel both ways, a channel that is not ours). Nothing is persisted.</exception>
    /// <exception cref="InvalidOperationException">A payment for the invoice's hash is already in flight or
    /// succeeded. Nothing is persisted.</exception>
    Task<PayInvoiceResult> PayInvoiceAsync(string bolt11, LightningMoney? amount, PayInvoiceOptions options,
                                           CancellationToken cancellationToken = default);

    /// <summary>
    /// Pays a recipient through one of its blinded paths (BOLT 4 "Route Blinding", sender side; ONION M5) and waits for
    /// the outcome.
    /// </summary>
    /// <remarks>
    /// The route runs to the path's introduction node like any other payment, for the amount plus the path's fee
    /// (<see cref="Protocol.Onion.Models.BlindedPayInfo.ComputeFeeMsat"/>) and with the path's CLTV delta as the final
    /// delta; the blinded hops follow with their <c>encrypted_recipient_data</c> (the introduction node also gets
    /// <c>current_path_key</c>) and the final one with <c>amt_to_forward</c>, <c>outgoing_cltv_value</c> and
    /// <c>total_amount_msat</c>. The payment is sent in one HTLC (no split); a failure from inside a blinded path moves
    /// to the next usable path. Persistence and outcome are those of
    /// <see cref="PayInvoiceAsync(string, LightningMoney?, PayInvoiceOptions, CancellationToken)"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">No path, an invalid amount, or an option out of range. Nothing is persisted.
    /// </exception>
    /// <exception cref="InvalidOperationException">A payment for the hash is already in flight or succeeded. Nothing
    /// is persisted.</exception>
    Task<PayInvoiceResult> PayBlindedAsync(PayBlindedRequest request, PayInvoiceOptions options,
                                           CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a spontaneous (keysend) payment and waits for the outcome.
    /// </summary>
    /// <remarks>
    /// A fresh CSPRNG preimage, payment hash = SHA256(preimage); the payee's hop payload carries <c>amt_to_forward</c>,
    /// <c>outgoing_cltv_value</c>, <c>keysend_preimage</c> (5482373484) and the request's custom records, and no
    /// <c>payment_data</c> (there is no invoice, so no <c>payment_secret</c>). The route is planned like an invoice
    /// payment without route hints (a direct channel, or the graph), with <c>Node:Keysend:FinalCltvExpiryDelta</c> as
    /// the final CLTV delta. Always one HTLC at a time (no split: keysend has no standard multi-part form and LND refuses
    /// multi-part keysend); retries as for an invoice. The payment is stored with <c>PaymentModel.Keysend</c> (its custom
    /// records). Persistence and outcome are those of
    /// <see cref="PayInvoiceAsync(string, LightningMoney?, PayInvoiceOptions, CancellationToken)"/>.
    /// </remarks>
    /// <param name="request">The payee, the amount and the custom records.</param>
    /// <param name="options">The per-call fee limit and timeout (<see cref="PayInvoiceOptions.MaxParts"/> is ignored).
    /// </param>
    /// <param name="cancellationToken">Stops waiting and retrying, like the timeout.</param>
    /// <exception cref="ArgumentException">The amount is zero, the payee is this node, a custom record is invalid, or an
    /// option is out of range. Nothing is persisted.</exception>
    Task<PayInvoiceResult> PayKeysendAsync(PayKeysendRequest request, PayInvoiceOptions options,
                                           CancellationToken cancellationToken = default);

    /// <summary>
    /// Pays over exactly the routes the caller supplied (<c>payroute</c>, NL-1082): they are offered as given, never
    /// re-planned or retried by the node — every outcome is reported per route and the caller decides what is next.
    /// </summary>
    /// <remarks>
    /// The identity is the request's BOLT 11 invoice (its hash, secret, amount and <c>basic_mpp</code> support are
    /// used) or a raw payment hash with an optional secret and an explicit total. More than one route (or one route
    /// delivering less than the total) is an MPP shard set: every route reports the same total and the payee holds
    /// the parts until it is reached. The routes are validated before anything is offered (shape, our first-hop
    /// channels, CLTV bounds, the fee limit, forwarding policies the graph knows and per-channel liquidity), then
    /// offered in order; a refusal or a decrypted failure ends that route only. Everything else behaves like
    /// <see cref="PayInvoiceAsync(string, LightningMoney?, PayInvoiceOptions, CancellationToken)"/>: one row per
    /// hash, part rows for restart-safe failure decryption, mission control learns from failures.
    /// </remarks>
    Task<PayRouteResult> PayRouteAsync(PayRouteRequest request, PayInvoiceOptions options,
                                       CancellationToken cancellationToken = default);

    /// <summary>
    /// The payment for <paramref name="paymentHash"/>, or null.
    /// </summary>
    Task<PaymentModel?> GetPaymentAsync(Hash paymentHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a call of this process is still paying <paramref name="paymentHash"/>: parts in flight or a retry to
    /// come. Its stored row can then read <c>Failed</c> between two attempts, which is not the payment's outcome
    /// (NL-999).
    /// </summary>
    bool IsPaying(Hash paymentHash) => false;

    /// <summary>
    /// Payments, newest first.
    /// </summary>
    Task<IReadOnlyList<PaymentModel>> ListPaymentsAsync(int skip, int take,
                                                        CancellationToken cancellationToken = default);
}