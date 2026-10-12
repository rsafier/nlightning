namespace NLightning.Domain.Payments.Interfaces;

using Crypto.ValueObjects;
using Enums;
using Models;

/// <summary>
/// Stores our outgoing payments, keyed by payment hash (one stored payment per hash: the latest attempt).
/// </summary>
/// <remarks>
/// <para>Writes are staged: they reach the database with <c>IUnitOfWork.SaveChangesAsync</c>. A payment is saved as
/// <c>InFlight</c> before its HTLC is offered, and its outcome is saved in the same unit of work as the channel
/// transition that resolved the HTLC, so a crash never leaves a resolved HTLC with an in-flight payment.</para>
/// <para>The HTLC id is recorded (<c>PaymentModel.AddOutgoingHtlc</c>) in a save after the one that added the HTLC,
/// so an <c>InFlight</c> payment from <see cref="GetInFlightAsync"/> without an outgoing HTLC may still have a live
/// one: fail it only after confirming that no channel HTLC carries <c>HtlcOrigin.Local(paymentHash)</c>.</para>
/// <para>Retries: a payment whose stored attempt is <c>Failed</c> may be retried; the retry's
/// <see cref="AddAsync"/> replaces the failed row (only the latest attempt is kept). A hash whose stored payment is
/// <c>InFlight</c> or <c>Succeeded</c> is never added again.</para>
/// </remarks>
public interface IPaymentDbRepository
{
    /// <summary>
    /// Stages a new payment attempt. The payment hash must be new, or its stored payment must be <c>Failed</c>: the
    /// new attempt then replaces it. Throws <see cref="InvalidOperationException"/> when the stored payment for the
    /// hash is <c>InFlight</c> or <c>Succeeded</c>.
    /// </summary>
    Task AddAsync(PaymentModel payment);

    /// <summary>
    /// Stages the payment's mutable fields (status, outgoing HTLC, preimage, failure, completion time, and the fee, which
    /// a failure sets to zero).
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

    /// <summary>
    /// Payments, newest first, without the outgoing legs of trampoline relays (<c>PaymentModel.IsTrampolineRelay</c>)
    /// unless <paramref name="includeTrampolineRelays"/> (NL-899). The default (test doubles) filters the page
    /// <see cref="ListAsync(int, int)"/> returns, so it may return fewer than <paramref name="take"/>.
    /// </summary>
    async Task<IReadOnlyList<PaymentModel>> ListAsync(int skip, int take, bool includeTrampolineRelays)
    {
        var page = await ListAsync(skip, take);
        return includeTrampolineRelays ? page : page.Where(p => !p.IsTrampolineRelay).ToList();
    }

    /// <summary>
    /// How many stored payments are outgoing legs of trampoline relays (NL-899). The default (test doubles) counts
    /// none.
    /// </summary>
    Task<int> CountTrampolineRelaysAsync() => Task.FromResult(0);

    /// <summary>
    /// A page of payments by <c>payment_index</c> (<see cref="LndIndexQuery"/>), without trampoline relay legs, only
    /// <c>Succeeded</c> ones when <paramref name="succeededOnly"/> (LND's <c>ListPayments</c>, NL-1163/NL-1165). The
    /// default (test doubles) reads every payment through <see cref="ListAsync(int, int)"/> and filters in memory.
    /// </summary>
    async Task<IReadOnlyList<PaymentModel>> ListByIndexAsync(LndIndexQuery query, bool succeededOnly)
    {
        var all = new List<PaymentModel>();
        for (var skip = 0; ; skip += 500)
        {
            var page = await ListAsync(skip, 500);
            all.AddRange(page);
            if (page.Count < 500)
                break;
        }

        var matching = all.Where(p => !p.IsTrampolineRelay && query.Contains(p.PaymentIndex, p.CreatedAt)
                                   && (!succeededOnly || p.Status == PaymentStatus.Succeeded));
        var ordered = query.Ascending
                          ? matching.OrderBy(p => p.PaymentIndex)
                          : matching.OrderByDescending(p => p.PaymentIndex);
        return ordered.Take(query.Take).ToList();
    }

    /// <summary>How many stored payments are not trampoline relay legs, only <c>Succeeded</c> ones when
    /// <paramref name="succeededOnly"/> (LND's <c>total_num_payments</c>). The default (test doubles) counts in
    /// memory.</summary>
    async Task<int> CountAsync(bool succeededOnly)
    {
        var count = 0;
        for (var skip = 0; ; skip += 500)
        {
            var page = await ListAsync(skip, 500);
            count += page.Count(p => !p.IsTrampolineRelay && (!succeededOnly || p.Status == PaymentStatus.Succeeded));
            if (page.Count < 500)
                return count;
        }
    }
}