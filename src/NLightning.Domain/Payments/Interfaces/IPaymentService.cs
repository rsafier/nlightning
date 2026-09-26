namespace NLightning.Domain.Payments.Interfaces;

using Crypto.ValueObjects;
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
    /// </remarks>
    /// <param name="bolt11">The invoice.</param>
    /// <param name="amount">The amount for an invoice without one; must be null (or equal) when the invoice sets it.</param>
    /// <param name="options">The per-call fee limit, part limit and timeout.</param>
    /// <param name="cancellationToken">Stops waiting and retrying, like the timeout.</param>
    /// <exception cref="ArgumentException">The invoice is malformed, expired, for another network, the amount is
    /// missing or inconsistent, or an option is out of range. Nothing is persisted.</exception>
    /// <exception cref="InvalidOperationException">A payment for the invoice's hash is already in flight or
    /// succeeded. Nothing is persisted.</exception>
    Task<PayInvoiceResult> PayInvoiceAsync(string bolt11, LightningMoney? amount, PayInvoiceOptions options,
                                           CancellationToken cancellationToken = default);

    /// <summary>
    /// The payment for <paramref name="paymentHash"/>, or null.
    /// </summary>
    Task<PaymentModel?> GetPaymentAsync(Hash paymentHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Payments, newest first.
    /// </summary>
    Task<IReadOnlyList<PaymentModel>> ListPaymentsAsync(int skip, int take,
                                                        CancellationToken cancellationToken = default);
}