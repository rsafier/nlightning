namespace NLightning.Domain.Offers.Interfaces;

using Models;

/// <summary>
/// Pays BOLT 12 offers (<c>payoffer</c>, <c>fetchinvoice</c>; lane B12-E).
/// </summary>
/// <remarks>
/// <para>Fetch: decode and validate the offer (BOLT 12 "Offers" reader, B12-OFR-03; an offer in another
/// <c>offer_currency</c> needs an explicit amount); build an invoice_request that copies every offer field raw and adds
/// random <c>invreq_metadata</c>, the amount and quantity when needed, the transient <c>invreq_payer_id</c> and the note,
/// signed with <see cref="IBolt12Signer.SignAsPayer"/> (B12-IRQ-01); send it with
/// <c>IOnionMessageService.SendAndWaitForReplyAsync</c> to an <c>offer_paths</c> path (the next one on each retry), else
/// to <c>offer_issuer_id</c>, expecting 66 or 68 (B12-OFR-04); verify the invoice (B12-INV-03/04/05: exact match of our
/// request's fields, <c>invoice_node_id</c>, signature, amount, expiry, paths).</para>
/// <para>Pay: <c>IPaymentService.PayBlindedAsync</c> over the invoice's <c>invoice_paths</c> with their
/// <c>invoice_blindedpay</c> (B12-PAY-01/02), the payment row carrying the BOLT 12 fields
/// (<c>PaymentModel.Bolt12</c>) and <c>invoice_node_id</c> as the payee.</para>
/// </remarks>
public interface IOfferPaymentService
{
    /// <summary>
    /// Whether paying offers works (<c>option_onion_messages</c> advertised). When false, both calls return
    /// <see cref="Enums.FetchInvoiceStatus.Unreachable"/>.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Fetches and verifies an invoice for the offer without paying it.
    /// </summary>
    /// <exception cref="ArgumentException">The offer string is malformed or invalid, expired or for another chain, or
    /// the amount or quantity breaks a BOLT 12 rule. Nothing is sent.</exception>
    Task<FetchInvoiceResult> FetchInvoiceAsync(PayOfferRequest request, PayOfferOptions options,
                                               CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches an invoice for the offer and pays it, then waits for the outcome as
    /// <c>IPaymentService.PayInvoiceAsync</c> does.
    /// </summary>
    /// <returns>The fetch outcome and, when an invoice was received, the payment.</returns>
    /// <exception cref="ArgumentException">As <see cref="FetchInvoiceAsync"/>. Nothing is sent or stored.</exception>
    /// <exception cref="InvalidOperationException">A payment for the invoice's hash is already in flight or
    /// succeeded.</exception>
    Task<PayOfferResult> PayOfferAsync(PayOfferRequest request, PayOfferOptions options,
                                       CancellationToken cancellationToken = default);
}