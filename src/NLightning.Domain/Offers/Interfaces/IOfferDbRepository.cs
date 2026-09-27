namespace NLightning.Domain.Offers.Interfaces;

using Crypto.ValueObjects;
using Models;

/// <summary>
/// Stores the offers we created, keyed by offer id (BOLT 12 plan §3.10, migration <c>AddBolt12Offers</c>, lane B12-C).
/// </summary>
/// <remarks>
/// Writes are staged: they reach the database with <c>IUnitOfWork.SaveChangesAsync</c>. BOLT 12 invoices we issue live
/// in <c>IInvoiceDbRepository</c> (<c>InvoiceModel.Bolt12.OfferId</c>); the counts below read them.
/// </remarks>
public interface IOfferDbRepository
{
    /// <summary>
    /// Stages a new offer. The offer id must be new.
    /// </summary>
    Task AddAsync(OfferModel offer);

    /// <summary>
    /// Stages the offer's mutable fields (status, disable time).
    /// </summary>
    Task UpdateAsync(OfferModel offer);

    /// <summary>
    /// The offer with <paramref name="offerId"/>, or null.
    /// </summary>
    Task<OfferModel?> GetByIdAsync(Hash offerId);

    /// <summary>
    /// The offer whose TLV stream is exactly <paramref name="offerBytes"/> (the offer fields an invoice_request copied,
    /// B12-IRQ-03), or null.
    /// </summary>
    Task<OfferModel?> GetByOfferBytesAsync(ReadOnlyMemory<byte> offerBytes);

    /// <summary>
    /// Offers, newest first.
    /// </summary>
    /// <param name="activeOnly">Only offers stored as <c>Active</c> (the caller checks the expiry).</param>
    /// <param name="skip">How many of the newest to skip.</param>
    /// <param name="take">The most to return.</param>
    Task<IReadOnlyList<OfferModel>> ListAsync(bool activeOnly, int skip, int take);

    /// <summary>
    /// How many BOLT 12 invoices of the offer are settled, and how many are open and unexpired at
    /// <paramref name="now"/> (the per-offer cap, plan D11).
    /// </summary>
    Task<OfferInvoiceCounts> GetInvoiceCountsAsync(Hash offerId, DateTimeOffset now);

    /// <summary>
    /// How many BOLT 12 invoices of any offer are open and unexpired at <paramref name="now"/> (the node-wide cap, plan
    /// D11).
    /// </summary>
    Task<int> CountUnpaidInvoicesAsync(DateTimeOffset now);
}