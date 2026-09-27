namespace NLightning.Domain.Offers.Interfaces;

using Crypto.ValueObjects;
using Models;

/// <summary>
/// Our offers (BOLT 12 "Offers" writer; <c>createoffer</c>, <c>listoffers</c>, <c>disableoffer</c>; lane B12-D).
/// </summary>
/// <remarks>
/// <para>Invoice_requests for our offers are answered by an <c>IOnionMessageHandler</c> for payload type 64
/// (<c>InvoiceRequestHandler</c>), not through this interface: it validates the request (BOLT 12 "Invoice Requests"
/// reader), stores a BOLT 12 <c>InvoiceModel</c> with blinded payment paths to us, then replies with the signed
/// invoice (66), or with an <c>invoice_error</c> (68) only after the request's signature verified (plan D10).</para>
/// <para>The offer is persisted (<see cref="IOfferDbRepository"/>) before its string is returned.</para>
/// </remarks>
public interface IOfferService
{
    /// <summary>
    /// Whether offers work: <c>option_onion_messages</c> and <c>option_route_blinding</c> are both advertised (we
    /// cannot be paid through a blinded path otherwise). When false, <see cref="CreateOfferAsync"/> throws.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Creates, stores and returns an offer: <c>offer_issuer_id</c> = our node id (plan D2), random
    /// <c>offer_metadata</c>, and <c>offer_paths</c> introduced by a peer when we have no public channel or when
    /// <see cref="CreateOfferRequest.ForcePaths"/> is set (B12-OFR-01/02).
    /// </summary>
    /// <exception cref="ArgumentException">The request breaks a BOLT 12 writer rule (an amount without a description,
    /// a zero amount, an expiry in the past). Nothing is stored.</exception>
    /// <exception cref="InvalidOperationException">Offers are not available, or paths are needed and no peer can
    /// introduce one. Nothing is stored.</exception>
    Task<OfferModel> CreateOfferAsync(CreateOfferRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The offer with <paramref name="offerId"/>, or null.
    /// </summary>
    Task<OfferModel?> GetOfferAsync(Hash offerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Our offers, newest first.
    /// </summary>
    /// <param name="activeOnly">Only offers that still answer invoice_requests.</param>
    /// <param name="skip">How many of the newest to skip.</param>
    /// <param name="take">The most to return.</param>
    /// <param name="cancellationToken">Stops the query.</param>
    Task<IReadOnlyList<OfferModel>> ListOffersAsync(bool activeOnly, int skip, int take,
                                                    CancellationToken cancellationToken = default);

    /// <summary>
    /// How many invoices we issued for the offer are paid and unpaid.
    /// </summary>
    Task<OfferInvoiceCounts> GetInvoiceCountsAsync(Hash offerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disables an offer: later invoice_requests for it are not answered with an invoice. Invoices already issued can
    /// still be paid until they expire.
    /// </summary>
    /// <returns>The offer as stored, or null when there is no such offer. An offer that is no longer active is
    /// returned unchanged.</returns>
    Task<OfferModel?> DisableOfferAsync(Hash offerId, CancellationToken cancellationToken = default);
}