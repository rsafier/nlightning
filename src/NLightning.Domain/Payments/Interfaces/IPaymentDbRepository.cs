namespace NLightning.Domain.Payments.Interfaces;

using Crypto.ValueObjects;
using Models;

/// <summary>
/// Stores our outgoing payments, keyed by payment hash (one payment per hash).
/// </summary>
/// <remarks>
/// Writes are staged: they reach the database with <c>IUnitOfWork.SaveChangesAsync</c>. A payment is saved as
/// <c>InFlight</c> before its HTLC is offered, and its outcome is saved in the same unit of work as the channel
/// transition that resolved the HTLC, so a crash never leaves a resolved HTLC with an in-flight payment.
/// </remarks>
public interface IPaymentDbRepository
{
    /// <summary>
    /// Stages a new payment. The payment hash must be new.
    /// </summary>
    Task AddAsync(PaymentModel payment);

    /// <summary>
    /// Stages the payment's mutable fields (status, outgoing HTLC, preimage, failure, completion time).
    /// </summary>
    Task UpdateAsync(PaymentModel payment);

    /// <summary>
    /// The payment for <paramref name="paymentHash"/>, or null.
    /// </summary>
    Task<PaymentModel?> GetByPaymentHashAsync(Hash paymentHash);

    /// <summary>
    /// Payments still <c>InFlight</c> (startup replay).
    /// </summary>
    Task<IReadOnlyList<PaymentModel>> GetInFlightAsync();

    /// <summary>
    /// Payments, newest first.
    /// </summary>
    /// <param name="skip">How many of the newest to skip.</param>
    /// <param name="take">The most to return.</param>
    Task<IReadOnlyList<PaymentModel>> ListAsync(int skip, int take);
}