namespace NLightning.Domain.Payments.Interfaces;

using Crypto.ValueObjects;
using Models;

/// <summary>
/// Stores the invoices we issued, keyed by payment hash.
/// </summary>
/// <remarks>
/// Writes are staged: they reach the database with <c>IUnitOfWork.SaveChangesAsync</c>, so an invoice status change
/// can commit in the same save as the channel transition that caused it (for example <c>Settled</c> with the
/// <c>revoke_and_ack</c> that made the fulfill irrevocable).
/// </remarks>
public interface IInvoiceDbRepository
{
    /// <summary>
    /// Stages a new invoice. The payment hash must be new.
    /// </summary>
    Task AddAsync(InvoiceModel invoice);

    /// <summary>
    /// Stages the invoice's mutable fields (status, amount received, settlement time).
    /// </summary>
    Task UpdateAsync(InvoiceModel invoice);

    /// <summary>
    /// The invoice for <paramref name="paymentHash"/>, or null when we never issued one.
    /// </summary>
    Task<InvoiceModel?> GetByPaymentHashAsync(Hash paymentHash);

    /// <summary>
    /// Invoices, newest first.
    /// </summary>
    /// <param name="skip">How many of the newest to skip.</param>
    /// <param name="take">The most to return.</param>
    Task<IReadOnlyList<InvoiceModel>> ListAsync(int skip, int take);

    /// <summary>
    /// The <c>Settled</c> invoices settled at or before <paramref name="settledAtOrBefore"/>, oldest settle first (then
    /// by payment hash): a set that only grows at its end and is never pruned, so skip paging over it is stable (the
    /// accounting backfill's memo pass, NL-602 A1-T6). The default is for test doubles that do not filter.
    /// </summary>
    Task<IReadOnlyList<InvoiceModel>> ListSettledAsync(DateTimeOffset settledAtOrBefore, int skip, int take) =>
        throw new NotSupportedException("This repository does not list settled invoices.");

    /// <summary>
    /// The <c>Settled</c> BOLT 12 invoices we issued for the offer <paramref name="offerId"/>, oldest settle first: every
    /// payment the offer received (NL-997, the CDK payment processor's <c>CheckIncomingPayment</c> by offer id). The
    /// default is for test doubles that store no BOLT 12 invoices.
    /// </summary>
    Task<IReadOnlyList<InvoiceModel>> ListSettledByOfferIdAsync(Hash offerId) =>
        throw new NotSupportedException("This repository does not list an offer's invoices.");

    /// <summary>
    /// Stages the deletion of at most <paramref name="max"/> BOLT 12 invoices that are still <c>Open</c> and expired at
    /// <paramref name="now"/> (<c>CreatedAt + ExpirySeconds &lt;= now</c>), oldest expiry first, and returns how many
    /// it staged (BOLT 12 plan §3.7 step 6, D11: every answered invoice_request adds a row, so the expired unpaid ones
    /// are swept to keep the table and the caps bounded).
    /// </summary>
    /// <remarks>
    /// BOLT 11 invoices and <c>Accepted</c>, <c>Settled</c> or <c>Canceled</c> rows are never touched. An HTLC that
    /// arrives later for a pruned invoice finds no invoice, which the final hop already treats like an expired one.
    /// The default is for test doubles that store no BOLT 12 invoices.
    /// </remarks>
    Task<int> PruneExpiredBolt12InvoicesAsync(DateTimeOffset now, int max) =>
        throw new NotSupportedException("This repository does not prune BOLT 12 invoices.");
}