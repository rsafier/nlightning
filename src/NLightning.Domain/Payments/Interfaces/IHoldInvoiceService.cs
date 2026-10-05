namespace NLightning.Domain.Payments.Interfaces;

using Crypto.ValueObjects;
using Models;

/// <summary>
/// The operator's side of hold invoices (NL-995, Cashu plan C4): settle a held set with the outside preimage or
/// cancel it, failing its parts back. Implemented by the HTLC switch (the sets are its state).
/// </summary>
public interface IHoldInvoiceService
{
    /// <summary>
    /// Settles a held invoice: <paramref name="preimage"/> (which must hash to the payment hash) is stored, the
    /// invoice settles in one save with its accounting event, and every held part is fulfilled with it.
    /// </summary>
    /// <exception cref="ArgumentException">No invoice for the hash, or the preimage does not hash to it.</exception>
    /// <exception cref="InvalidOperationException">The invoice is not <c>Held</c>, or its set lost parts (then it is
    /// canceled instead).</exception>
    Task<InvoiceModel> SettleHoldInvoiceAsync(Hash paymentHash, Secret preimage,
                                              CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a hold invoice: a held set's parts are failed back with
    /// <c>incorrect_or_unknown_payment_details</c> (as a canceled invoice's would be) and the row is
    /// <c>Canceled</c>; an open one is simply canceled.
    /// </summary>
    Task<InvoiceModel> CancelHoldInvoiceAsync(Hash paymentHash, CancellationToken cancellationToken = default);
}