namespace NLightning.Domain.Payments.Interfaces;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Models;

/// <summary>
/// Stores the parts (offered HTLCs) of our outgoing payments with their routes (NL-321), one row per offered part,
/// keyed by (payment hash, part index).
/// </summary>
/// <remarks>
/// <para>Writes are staged: they reach the database with <c>IUnitOfWork.SaveChangesAsync</c>. A part row is added in
/// the save that follows its offer (with its HTLC id), so a crash before it loses nothing that was not lost already:
/// the payment row still records one part, and every offered HTLC carries <c>HtlcOrigin.Local(paymentHash)</c>.</para>
/// <para>A retry that replaces the stored payment row (a new attempt) starts from an empty part list
/// (<see cref="DeleteForPaymentAsync"/>); a part that changes while the row stays (it resolved) is an
/// <see cref="UpdateAsync"/> of the same row.</para>
/// </remarks>
public interface IPaymentPartDbRepository
{
    /// <summary>
    /// Stages a new part row. The (payment hash, part index) must be new.
    /// </summary>
    Task AddAsync(PaymentPartModel part);

    /// <summary>
    /// Stages the part's mutable fields (state, per-hop hold times).
    /// </summary>
    Task UpdateAsync(PaymentPartModel part);

    /// <summary>
    /// The parts of <paramref name="paymentHash"/> in offer order (empty when none was stored).
    /// </summary>
    Task<IReadOnlyList<PaymentPartModel>> GetForPaymentAsync(Hash paymentHash);

    /// <summary>
    /// The part offered as HTLC <paramref name="htlcId"/> on <paramref name="channelId"/>, or null.
    /// </summary>
    Task<PaymentPartModel?> GetByHtlcAsync(Hash paymentHash, ChannelId channelId, ulong htlcId);

    /// <summary>
    /// Stages the removal of every part row of <paramref name="paymentHash"/> (a retry replaces the attempt).
    /// </summary>
    Task DeleteForPaymentAsync(Hash paymentHash);
}