namespace NLightning.Domain.Payments.Interfaces;

using Crypto.ValueObjects;
using Trampoline;

/// <summary>
/// Stores the trampoline routes of our own payments sent through trampoline nodes (payer side, NL-875): the hops of
/// each attempt's trampoline onion with their shared secrets, so a failure returned by a trampoline node can be
/// decrypted after a restart.
/// </summary>
/// <remarks>
/// Writes are staged and committed by <c>IUnitOfWork.SaveChangesAsync</c>; save an attempt's hops before its first HTLC
/// is offered. No foreign key to <c>Payments</c>: a retry replaces the payment row, while the hops of every attempt
/// stay.
/// </remarks>
public interface IPaymentTrampolineHopDbRepository
{
    /// <summary>Stages the hops of one or more attempts (keyed by payment hash, attempt and hop index).</summary>
    /// <exception cref="InvalidOperationException">A hop with the same key exists.</exception>
    Task AddRangeAsync(IEnumerable<PaymentTrampolineHopModel> hops);

    /// <summary>
    /// The trampoline hops of the payment <paramref name="paymentHash"/>, ordered by attempt then hop index: every
    /// attempt's, or only <paramref name="attempt"/>'s when given.
    /// </summary>
    Task<IReadOnlyList<PaymentTrampolineHopModel>> GetByPaymentAsync(Hash paymentHash, int? attempt = null);
}