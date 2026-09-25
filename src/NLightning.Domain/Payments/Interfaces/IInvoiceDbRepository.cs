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
}